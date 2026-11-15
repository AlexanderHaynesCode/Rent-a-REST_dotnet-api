using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
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

        // Hidden menus (and their categories) stay off the storefront.
        var menus = await dbContext.Menus
            .AsNoTracking()
            .Where(x => x.IsVisible)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var visibleMenuIds = menus.Select(x => x.Id).ToList();

        var categories = await dbContext.MenuCategories
            .AsNoTracking()
            .Where(x => visibleMenuIds.Contains(x.MenuId))
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var items = await dbContext.MenuItems
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The visitor's local date can be a day either side of the UTC date, and the storefront also
        // needs the day before (hours running past midnight) and the day after ("Tomorrow's hours"),
        // so the dated list spans yesterday through a week from today.
        var dailyWindowStart = today.AddDays(-1);
        var dailyWindowEnd = today.AddDays(7);

        var allHours = await dbContext.BusinessHours
            .AsNoTracking()
            .Where(h => h.Date == null || (h.Date >= dailyWindowStart && h.Date <= dailyWindowEnd))
            .ToListAsync(cancellationToken);

        BusinessHour? HoursOn(DateOnly date) =>
            allHours.FirstOrDefault(h => h.Date == date) ??
            allHours.FirstOrDefault(h => h.Date == null && h.DayOfWeek == date.DayOfWeek);

        var resolvedHours = Enumerable.Range(0, 7)
            .Select(offset => HoursOn(today.AddDays(offset)))
            .Where(h => h is not null)
            .ToList();

        var dailyHours = Enumerable.Range(0, dailyWindowEnd.DayNumber - dailyWindowStart.DayNumber + 1)
            .Select(offset => dailyWindowStart.AddDays(offset))
            .Select(date => (Date: date, Hour: HoursOn(date)))
            .Where(x => x.Hour is not null)
            .Select(x => new DailyHourResponse(
                x.Date,
                x.Date.DayOfWeek,
                x.Hour!.OpenTime.ToString("HH:mm"),
                x.Hour.CloseTime.ToString("HH:mm"),
                x.Hour.IsClosed))
            .ToList();

        var menu = categories
            .Select(category => new PublicMenuCategoryResponse(
                category.Id,
                category.Name,
                category.Description,
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
                    .ToList(),
                category.MenuId))
            .ToList();

        return Ok(new PublicRestaurantResponse(
            tenant.Id,
            tenant.Slug,
            profile.DisplayName,
            profile.Tagline,
            profile.Announcement,
            profile.PrimaryHexColor,
            profile.SecondaryHexColor,
            profile.LogoUrl,
            profile.HeroImageUrl,
            profile.PrimaryCtaUrl,
            profile.Address,
            profile.PhoneNumber,
            menu,
            resolvedHours.Select(hour => new BusinessHourResponse(
                hour!.DayOfWeek,
                hour.OpenTime.ToString("HH:mm"),
                hour.CloseTime.ToString("HH:mm"),
                hour.IsClosed)).ToList(),
            UseLegacyHoursStyle: profile.UseLegacyHoursStyle,
            ShowBottomHoursSection: profile.ShowBottomHoursSection,
            UseLegacyBottomHoursStyle: profile.UseLegacyBottomHoursStyle,
            ShowCustomBackground: profile.ShowCustomBackground,
            CustomBackgroundHexColor1: profile.CustomBackgroundHexColor1,
            CustomBackgroundHexColor2: profile.CustomBackgroundHexColor2,
            CustomBackgroundPosition1X: profile.CustomBackgroundPosition1X,
            CustomBackgroundPosition1Y: profile.CustomBackgroundPosition1Y,
            CustomBackgroundPosition2X: profile.CustomBackgroundPosition2X,
            CustomBackgroundPosition2Y: profile.CustomBackgroundPosition2Y,
            CustomBackgroundMainHexColor: profile.CustomBackgroundMainHexColor,
            CustomBackgroundMainColorActive: profile.CustomBackgroundMainColorActive,
            CustomBackgroundSplash1Active: profile.CustomBackgroundSplash1Active,
            CustomBackgroundSplash2Active: profile.CustomBackgroundSplash2Active,
            CustomBackgroundImageUrl: profile.CustomBackgroundImageUrl,
            CustomBackgroundImageActive: profile.CustomBackgroundImageActive,
            ShowTopHoursSection: profile.ShowTopHoursSection,
            TopHoursStyle: profile.TopHoursStyle,
            TopHoursOpenStatusDetail: profile.TopHoursOpenStatusDetail,
            DailyHours: dailyHours,
            Menus: menus
                .Select(m => new PublicMenuResponse(m.Id, m.Name, m.Description, m.SortOrder, m.IsVisible))
                .ToList(),
            AllowMenuPdfDownload: profile.AllowMenuPdfDownload));
    }
}
