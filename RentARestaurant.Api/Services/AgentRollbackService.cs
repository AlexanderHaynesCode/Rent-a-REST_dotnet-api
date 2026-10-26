using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

public enum AgentRollbackOutcome
{
    RolledBack,
    NothingToRollBack,
    NotFound,
    AlreadyRolledBack,
    Expired
}

public sealed record AgentRollbackResult(
    AgentRollbackOutcome Outcome,
    Guid? AuditId,
    DateTime? OriginalChangeAppliedUtc,
    IReadOnlyList<string> RevertedSummary,
    string? Reason)
{
    public bool Succeeded => Outcome == AgentRollbackOutcome.RolledBack;
}

public interface IAgentRollbackService
{
    /// <summary>
    /// Restores the tenant's site to how it looked before an agent change was applied.
    /// <paramref name="auditId"/> null selects the most recent audit that is neither rolled back nor expired.
    /// </summary>
    Task<AgentRollbackResult> RollbackAsync(Guid tenantId, Guid? auditId, CancellationToken cancellationToken);
}

public class AgentRollbackService(
    AppDbContext dbContext,
    IRestaurantAdminService restaurantAdminService) : IAgentRollbackService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentRollbackResult> RollbackAsync(Guid tenantId, Guid? auditId, CancellationToken cancellationToken)
    {
        // The restore and the rolled_back flags commit together, so a failure part-way leaves neither behind.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;

        AgentChangeAudit? target;
        if (auditId is { } requestedId)
        {
            target = await dbContext.AgentChangeAudits
                .SingleOrDefaultAsync(x => x.Id == requestedId && x.TenantId == tenantId, cancellationToken);

            if (target is null)
            {
                return Failure(AgentRollbackOutcome.NotFound, "We couldn't find a change with that reference ID on your website.");
            }

            if (target.RolledBack)
            {
                return Failure(AgentRollbackOutcome.AlreadyRolledBack, "That change has already been rolled back.");
            }

            if (now > target.RollbackExpiresUtc)
            {
                return Failure(AgentRollbackOutcome.Expired, "The 30-day rollback window for that change has passed.");
            }
        }
        else
        {
            target = await dbContext.AgentChangeAudits
                .Where(x => x.TenantId == tenantId && !x.RolledBack && x.RollbackExpiresUtc > now)
                .OrderByDescending(x => x.AppliedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (target is null)
            {
                return Failure(
                    AgentRollbackOutcome.NothingToRollBack,
                    "There are no recent changes to undo. Changes can only be rolled back for 30 days after they are applied.");
            }
        }

        var snapshot = JsonSerializer.Deserialize<PublicRestaurantResponse>(target.PreChangeSnapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize pre-change snapshot.");

        await restaurantAdminService.RestoreSnapshotAsync(tenantId, snapshot, cancellationToken);

        // The site now predates every change applied at or after the target, so none of those can be rolled back again
        // (restoring one of their snapshots would bring back changes that were just undone).
        var undoneAudits = await dbContext.AgentChangeAudits
            .Where(x => x.TenantId == tenantId && !x.RolledBack && x.AppliedUtc >= target.AppliedUtc)
            .OrderByDescending(x => x.AppliedUtc)
            .ToListAsync(cancellationToken);

        var revertedSummary = new List<string>();
        foreach (var audit in undoneAudits)
        {
            audit.RolledBack = true;
            audit.RolledBackUtc = now;
            revertedSummary.AddRange(
                JsonSerializer.Deserialize<List<string>>(audit.DiffSummaryJson, JsonOptions) ?? []);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AgentRollbackResult(AgentRollbackOutcome.RolledBack, target.Id, target.AppliedUtc, revertedSummary, null);
    }

    private static AgentRollbackResult Failure(AgentRollbackOutcome outcome, string reason) =>
        new(outcome, null, null, [], reason);
}
