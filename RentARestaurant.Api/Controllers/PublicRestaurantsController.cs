using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Infrastructure.Tenancy;

namespace RentARestaurant.Api.Controllers;

[ApiController]
[Route("api/public/restaurants")]
public class PublicRestaurantsController(AppDbContext dbContext, ITenantContext tenantContext) : ControllerBase
{
    [HttpGet("{slug}")]
    [RequireTenantContext]
    public async Task<ActionResult<PublicRestaurantResponse>> GetBySlug([FromRoute] string slug, CancellationToken cancellationToken)
    {
        var resolvedSlug = tenantContext.TenantSlug;

        if (!string.Equals(slug, resolvedSlug, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var profile = await dbContext.RestaurantProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        if (profile is null)
        {
            return NotFound();
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstAsync(x => x.Id == tenantContext.TenantId!.Value, cancellationToken);

        var categories = await dbContext.MenuCategories
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var items = await dbContext.MenuItems
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowEnd = today.AddDays(6);

        var allHours = await dbContext.BusinessHours
            .AsNoTracking()
            .Where(h => h.Date == null || (h.Date >= today && h.Date <= windowEnd))
            .ToListAsync(cancellationToken);

        var resolvedHours = Enumerable.Range(0, 7)
            .Select(offset => today.AddDays(offset))
            .Select(date =>
                allHours.FirstOrDefault(h => h.Date == date) ??
                allHours.FirstOrDefault(h => h.Date == null && h.DayOfWeek == date.DayOfWeek))
            .Where(h => h is not null)
            .ToList();

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

        return Ok(new PublicRestaurantResponse(
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
            resolvedHours.Select(hour => new BusinessHourResponse(
                hour!.DayOfWeek,
                hour.OpenTime.ToString("HH:mm"),
                hour.CloseTime.ToString("HH:mm"),
                hour.IsClosed)).ToList()));
    }
}
