using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Storage;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

[ApiController]
[Route("api/admin/restaurant")]
public class AdminRestaurantController(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    ITenantAccessService tenantAccessService,
    IR2StorageService r2StorageService) : ControllerBase
{
    [HttpGet]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<PublicRestaurantResponse>> GetCurrent(CancellationToken cancellationToken)
    {
        var data = await BuildRestaurantResponseAsync(tenantContext.TenantId!.Value, cancellationToken);
        return Ok(data);
    }

    [HttpGet("bootstrap")]
    [RequireAdminUser]
    public async Task<ActionResult<AdminBootstrapResponse>> GetBootstrap(CancellationToken cancellationToken)
    {
        try
        {
            var externalUserId = HttpContext.Request.Headers["X-Admin-User-Id"].ToString().Trim();
            var resolution = await tenantAccessService.ResolveSingleTenantForAdminAsync(externalUserId, cancellationToken);

            if (resolution.Status == TenantResolutionStatus.NotFound || resolution.Tenant is null)
            {
                return NotFound(new { Error = "No active tenant membership found for this admin user." });
            }

            if (resolution.Status == TenantResolutionStatus.MultipleMatches)
            {
                return Conflict(new
                {
                    Error = "Multiple tenant memberships found for this admin user.",
                    Matches = resolution.MatchCount
                });
            }

            tenantContext.SetTenant(resolution.Tenant.TenantId, resolution.Tenant.Slug);

            var restaurant = await BuildRestaurantResponseAsync(resolution.Tenant.TenantId, cancellationToken);
            var response = new AdminBootstrapResponse(
                new AdminTenantSummaryResponse(
                    resolution.Tenant.TenantId,
                    resolution.Tenant.Slug,
                    resolution.Tenant.Name,
                    resolution.Tenant.CustomDomain,
                    resolution.Tenant.IsActive,
                    resolution.Tenant.SubscriptionState),
                restaurant);

            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "DERP An unexpected error occurred during bootstrap.", Details = ex.Message });
        }        
    }

    [HttpPut("branding")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpdateBranding([FromBody] UpdateBrandingRequest request, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RestaurantProfiles.SingleOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        profile.DisplayName = request.DisplayName.Trim();
        profile.Tagline = request.Tagline.Trim();
        profile.PrimaryHexColor = request.PrimaryHexColor.Trim();
        profile.SecondaryHexColor = request.SecondaryHexColor.Trim();
        profile.LogoUrl = request.LogoUrl?.Trim();
        profile.HeroImageUrl = request.HeroImageUrl?.Trim();
        profile.PrimaryCtaUrl = request.PrimaryCtaUrl?.Trim();
        profile.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("hours")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpsertHours([FromBody] IReadOnlyList<UpsertBusinessHourRequest> request, CancellationToken cancellationToken)
    {
        var existing = await dbContext.BusinessHours.ToListAsync(cancellationToken);

        foreach (var hour in request)
        {
            var target = existing.FirstOrDefault(x => x.DayOfWeek == hour.DayOfWeek);
            if (target is null)
            {
                dbContext.BusinessHours.Add(new BusinessHour
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantContext.TenantId!.Value,
                    DayOfWeek = hour.DayOfWeek,
                    OpenTime = hour.OpenTime,
                    CloseTime = hour.CloseTime,
                    IsClosed = hour.IsClosed
                });
            }
            else
            {
                target.OpenTime = hour.OpenTime;
                target.CloseTime = hour.CloseTime;
                target.IsClosed = hour.IsClosed;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("images/{imageType}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UploadBrandingImage(
        string imageType,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logo", "hero", "primary-cta" };
        if (!allowedTypes.Contains(imageType))
        {
            return BadRequest(new { Error = "imageType must be one of: logo, hero, primary-cta." });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { Error = "No file provided." });
        }

        var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png" };
        if (!allowedContentTypes.Contains(file.ContentType))
        {
            return BadRequest(new { Error = "Only JPEG and PNG images are accepted." });
        }

        const long maxBytes = 10 * 1024 * 1024; // 10 MB
        if (file.Length > maxBytes)
        {
            return BadRequest(new { Error = "File size must not exceed 10 MB." });
        }

        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantContext.TenantId!.Value, cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        string url;
        try
        {
            await using (var stream = file.OpenReadStream())
            {
                url = await r2StorageService.UploadImageAsync(
                    tenantContext.TenantId!.Value,
                    imageType,
                    stream,
                    file.ContentType,
                    cancellationToken);
            }
        } 
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to upload image.", Details = ex.Message });
        }
        

        switch (imageType)
        {
            case "logo":       profile.LogoUrl = url;       break;
            case "hero":       profile.HeroImageUrl = url;  break;
            case "primary-cta": profile.PrimaryCtaUrl = url; break;
        }

        profile.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { url });
    }

    [HttpPost("menu/categories")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<Guid>> CreateCategory([FromBody] CreateMenuCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = new MenuCategory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantContext.TenantId!.Value,
            Name = request.Name.Trim(),
            SortOrder = request.SortOrder
        };

        dbContext.MenuCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetCurrent), new { }, category.Id);
    }

    [HttpPost("menu/items")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<Guid>> CreateMenuItem([FromBody] CreateMenuItemRequest request, CancellationToken cancellationToken)
    {
        var categoryExists = await dbContext.MenuCategories.AnyAsync(x => x.Id == request.CategoryId, cancellationToken);
        if (!categoryExists)
        {
            return BadRequest(new { Error = "Category does not exist for this tenant." });
        }

        var item = new MenuItem
        {
            Id = Guid.NewGuid(),
            TenantId = tenantContext.TenantId!.Value,
            CategoryId = request.CategoryId,
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            Price = request.Price,
            IsAvailable = request.IsAvailable,
            SortOrder = request.SortOrder
        };

        dbContext.MenuItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetCurrent), new { }, item.Id);
    }

    private async Task<PublicRestaurantResponse> BuildRestaurantResponseAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RestaurantProfiles
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        if (profile is null)
        {
            throw new InvalidOperationException($"Restaurant profile not found for tenant {tenantId}. Provisioning may be incomplete.");
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .SingleAsync(x => x.Id == tenantId, cancellationToken);

        var categories = await dbContext.MenuCategories
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var items = await dbContext.MenuItems
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var hours = await dbContext.BusinessHours
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(x => x.DayOfWeek)
            .ToListAsync(cancellationToken);

        var menu = categories
            .Select(category => new PublicMenuCategoryResponse(
                category.Id,
                category.Name,
                category.SortOrder,
                items.Where(item => item.CategoryId == category.Id)
                    .OrderBy(item => item.SortOrder)
                    .Select(item => new PublicMenuItemResponse(
                        item.Id,
                        item.Name,
                        item.Description,
                        item.Price,
                        item.IsAvailable,
                        item.SortOrder))
                    .ToList()))
            .ToList();

        return new PublicRestaurantResponse(
            tenant.Id,
            tenant.Slug,
            profile.DisplayName,
            profile.Tagline,
            profile.PrimaryHexColor,
            profile.SecondaryHexColor,
            profile.LogoUrl,
            profile.HeroImageUrl,
            profile.PrimaryCtaUrl,
            menu,
            hours.Select(hour => new BusinessHourResponse(
                hour.DayOfWeek,
                hour.OpenTime.ToString("HH:mm"),
                hour.CloseTime.ToString("HH:mm"),
                hour.IsClosed)).ToList());
    }
}
