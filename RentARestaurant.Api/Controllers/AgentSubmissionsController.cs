using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Agent;
using RentARestaurant.Api.Infrastructure.Email;
using RentARestaurant.Api.Infrastructure.Tenancy;

namespace RentARestaurant.Api.Controllers;

/// <summary>
/// Tracks inbound agent submissions (email/SMS) end-to-end through translate -> validate -> apply.
/// Internal API consumed only by agent-intake-service and agent-validator-service.
/// </summary>
[ApiController]
[Route("api/agent/submissions")]
[RequireInternalAgentKey]
[RequireTenantContext]
public class AgentSubmissionsController(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    IEmailService emailService,
    IOptions<AgentOptions> agentOptions,
    IMemoryCache memoryCache) : ControllerBase
{
    private static readonly object RateLimitNoticeLock = new();
    [HttpPost]
    public async Task<ActionResult<AgentSubmissionResponse>> Create(
        [FromBody] CreateAgentSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;

        var limit = agentOptions.Value.MaxSubmissionsPerHour;
        var windowStartUtc = DateTime.UtcNow.AddHours(-1);
        var recentCount = await dbContext.AgentSubmissions
            .CountAsync(x => x.TenantId == tenantId && x.ReceivedUtc >= windowStartUtc, cancellationToken);

        if (recentCount >= limit)
        {
            await NotifyRateLimitedAsync(tenantId, limit, cancellationToken);
            Response.Headers.RetryAfter = "3600";
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { Error = $"Submission limit of {limit} per hour reached." });
        }

        var submission = new AgentSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Channel = request.Channel,
            SenderIdentifier = request.SenderIdentifier,
            RawBodyText = request.RawBodyText,
            AttachmentRefs = request.AttachmentRefs is { Count: > 0 }
                ? JsonSerializer.Serialize(request.AttachmentRefs)
                : null,
            Status = AgentSubmissionStatus.Received,
            ReceivedUtc = DateTime.UtcNow
        };

        dbContext.AgentSubmissions.Add(submission);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = submission.Id }, ToResponse(submission));
    }

    /// <summary>
    /// Looks up the most recent submission still awaiting tenant clarification from this sender, so
    /// a reply email can be merged with the original proposed change-set instead of starting cold.
    /// </summary>
    [HttpGet("pending-clarification")]
    public async Task<ActionResult<AgentPendingClarificationResponse>> GetPendingClarification(
        [FromQuery] string senderIdentifier,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;

        var submission = await dbContext.AgentSubmissions
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.SenderIdentifier == senderIdentifier
                && x.Status == AgentSubmissionStatus.NeedsClarification)
            .OrderByDescending(x => x.ReceivedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (submission is null)
        {
            return NoContent();
        }

        return Ok(new AgentPendingClarificationResponse(
            submission.Id,
            submission.TranslatedJson,
            submission.RejectionReason,
            submission.ReceivedUtc));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AgentSubmissionResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;
        var submission = await dbContext.AgentSubmissions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);

        if (submission is null)
        {
            return NotFound();
        }

        return Ok(ToResponse(submission));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateAgentSubmissionStatusRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;
        var submission = await dbContext.AgentSubmissions
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);

        if (submission is null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<AgentSubmissionStatus>(request.Status, ignoreCase: true, out var status))
        {
            return BadRequest(new { Error = $"Invalid status '{request.Status}'." });
        }

        submission.Status = status;
        submission.TranslatedJson = request.TranslatedJson ?? submission.TranslatedJson;
        submission.ValidatorConfidence = request.ValidatorConfidence ?? submission.ValidatorConfidence;
        submission.RejectionReason = request.RejectionReason ?? submission.RejectionReason;
        submission.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        if (status == AgentSubmissionStatus.NeedsClarification && !string.IsNullOrWhiteSpace(submission.RejectionReason))
        {
            await NotifyClarificationNeededAsync(tenantId, submission.RejectionReason, cancellationToken);
        }

        return NoContent();
    }

    private async Task NotifyRateLimitedAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        // In-memory, so a restart or extra instance can allow one extra notice per hour.
        lock (RateLimitNoticeLock)
        {
            var cacheKey = $"agent-rate-limit-notice:{tenantId}";
            if (memoryCache.TryGetValue(cacheKey, out _))
            {
                return;
            }

            memoryCache.Set(cacheKey, true, TimeSpan.FromHours(1));
        }

        var tenant = await dbContext.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
        var ownerEmail = await dbContext.TenantUsers
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Role == "Owner" ? 0 : 1)
            .Select(x => x.Email)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null || string.IsNullOrWhiteSpace(ownerEmail))
        {
            return;
        }

        await emailService.SendAgentRateLimitedEmailAsync(ownerEmail, tenant.Name, limit, cancellationToken);
    }

    private async Task NotifyClarificationNeededAsync(Guid tenantId, string rejectionReason, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
        var ownerEmail = await dbContext.TenantUsers
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Role == "Owner" ? 0 : 1)
            .Select(x => x.Email)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null || string.IsNullOrWhiteSpace(ownerEmail))
        {
            return;
        }

        var questions = rejectionReason
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToList();

        if (questions.Count == 0)
        {
            questions.Add(rejectionReason);
        }

        await emailService.SendAgentClarificationNeededEmailAsync(ownerEmail, tenant.Name, questions, cancellationToken);
    }

    private static AgentSubmissionResponse ToResponse(AgentSubmission submission) => new(
        submission.Id,
        submission.TenantId,
        submission.Channel,
        submission.Status.ToString(),
        submission.ReceivedUtc);
}
