using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Email;
using RentARestaurant.Api.Infrastructure.Storage;

namespace RentARestaurant.Api.Services;

public class TenantProvisioningService(
    AppDbContext dbContext,
    IMediaNamespaceProvisioner mediaNamespaceProvisioner,
    IEmailService emailService,
    ILogger<TenantProvisioningService> logger) : ITenantProvisioningService
{
    private static readonly Regex SlugSanitizerRegex = new("[^a-z0-9-]", RegexOptions.Compiled);

    public async Task<ProvisionTenantResponse> ProvisionTenantAsync(ProvisionTenantRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentException("ProvisionTenantRequest is required.");
        }

        if (string.IsNullOrWhiteSpace(request.RestaurantName))
        {
            throw new ArgumentException("RestaurantName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.SubscriptionPlan))
        {
            throw new ArgumentException("SubscriptionPlan is required.");
        }

        var normalizedPlan = request.SubscriptionPlan.Trim();
        if (normalizedPlan != SubscriptionPlans.SelfService
            && normalizedPlan != SubscriptionPlans.DoneForYou)
        {
            throw new ArgumentException("SubscriptionPlan must be either 'Self-Service' or 'Done-For-You'.");
        }

        if (string.IsNullOrWhiteSpace(request.OwnerExternalUserId))
        {
            throw new ArgumentException("OwnerExternalUserId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OwnerEmail))
        {
            throw new ArgumentException("OwnerEmail is required.");
        }

        var externalUserId = request.OwnerExternalUserId.Trim();

        var existingMembership = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(x => x.ExternalUserId.Trim() == externalUserId, cancellationToken);
        if (existingMembership)
        {
            throw new ArgumentException("OwnerExternalUserId is already assigned to a tenant.");
        }

        var slug = await BuildUniqueSlugAsync(request.RequestedSlug ?? request.RestaurantName, cancellationToken);
        var tenantId = Guid.NewGuid();

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = request.RestaurantName.Trim(),
            Slug = slug,
            CustomDomain = request.CustomDomain,
            IsActive = true,
            SubscriptionPlan = normalizedPlan,
            SubscriptionState = string.IsNullOrWhiteSpace(request.StripeSubscriptionId) ? "trialing" : "active"
        };

        var tenantUser = new TenantUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExternalUserId = externalUserId,
            Email = request.OwnerEmail.Trim(),
            Role = "Owner" // or "Admin" or whatever
        };

        var profile = new RestaurantProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = tenant.Name,
            Tagline = "",
            PrimaryHexColor = "#222222",
            SecondaryHexColor = "#ffffff"
        };

        var businessHours = new List<BusinessHour>();
        for (var i = 0; i < 7; i++)        {
            businessHours.Add(new BusinessHour
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DayOfWeek = (DayOfWeek)i,
                OpenTime = new TimeOnly(11, 0),
                CloseTime = new TimeOnly(22, 0),
                IsClosed = i == 1
            });
        }        

        dbContext.Tenants.Add(tenant);
        dbContext.TenantUsers.Add(tenantUser);
        dbContext.RestaurantProfiles.Add(profile);
        dbContext.BusinessHours.AddRange(businessHours);

        await dbContext.SaveChangesAsync(cancellationToken);

        var mediaNamespace = await mediaNamespaceProvisioner.CreateTenantNamespaceAsync(tenantId, cancellationToken);

        logger.LogInformation("Provisioned tenant {TenantId} with slug {Slug}", tenantId, slug);

        try
        {
            await emailService.SendWelcomeEmailAsync(request.OwnerEmail.Trim(), request.RestaurantName.Trim(), slug, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send welcome email to {Email} for tenant {TenantId}", request.OwnerEmail, tenantId);
        }

        return new ProvisionTenantResponse(tenantId, slug, tenant.IsActive, mediaNamespace);
    }

    private async Task<string> BuildUniqueSlugAsync(string rawInput, CancellationToken cancellationToken)
    {
        var normalized = rawInput.Trim().ToLowerInvariant().Replace(' ', '-');
        normalized = SlugSanitizerRegex.Replace(normalized, string.Empty);
        normalized = normalized.Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "restaurant";
        }

        var slug = normalized;
        var counter = 1;

        while (await dbContext.Tenants.AsNoTracking().AnyAsync(x => x.Slug == slug, cancellationToken))
        {
            counter++;
            slug = $"{normalized}-{counter}";
        }

        return slug;
    }
}
