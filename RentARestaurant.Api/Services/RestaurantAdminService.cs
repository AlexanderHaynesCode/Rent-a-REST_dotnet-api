using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain;
using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

public enum MenuItemMutationStatus
{
    Success,
    NotFound,
    CategoryInvalid,
    LimitReached
}

public sealed record MenuItemMutationResult(MenuItemMutationStatus Status, Guid? ItemId = null);

/// <summary>Outcome of a menu or menu-category write.</summary>
public enum MenuMutationStatus
{
    Success,
    NotFound,
    LimitReached,
    DuplicateName,
    MenuInvalid,
    LastMenu
}

public sealed record MenuMutationResult(MenuMutationStatus Status, Guid? Id = null);

public enum MenuCloneStatus
{
    Success,
    NotFound,
    DuplicateName,
    LimitReached
}

/// <summary>
/// Outcome of copying a menu. <see cref="Copy"/> is set on success; <see cref="LimitProblems"/> holds the
/// tenant-facing reasons when the copy would go past a menu, category or item limit.
/// </summary>
public sealed record MenuCloneResult(
    MenuCloneStatus Status,
    string? SourceName = null,
    ClonedMenuResponse? Copy = null,
    IReadOnlyList<string>? LimitProblems = null);

/// <summary>
/// Shared read/write logic for a tenant's restaurant profile, menu, and hours. Used by both the
/// human-facing <c>AdminRestaurantController</c> and the internal <c>AgentRestaurantController</c>
/// so the two callers can never diverge in behavior.
/// </summary>
public interface IRestaurantAdminService
{
    Task<PublicRestaurantResponse> GetSnapshotAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<bool> UpdateBrandingAsync(Guid tenantId, UpdateBrandingRequest request, CancellationToken cancellationToken);

    Task UpsertHoursAsync(Guid tenantId, IReadOnlyList<UpsertBusinessHourRequest> request, CancellationToken cancellationToken);

    Task<bool> SetTopHoursSectionAsync(Guid tenantId, SetTopHoursSectionRequest request, CancellationToken cancellationToken);

    Task<bool> SetBottomHoursSectionAsync(Guid tenantId, bool showBottomHoursSection, bool useLegacyBottomHoursStyle, CancellationToken cancellationToken);

    Task UpsertDateSpecificHoursAsync(Guid tenantId, IReadOnlyList<UpsertDateSpecificHourRequest> request, CancellationToken cancellationToken);

    Task<bool> DeleteDateSpecificHourAsync(Guid tenantId, DateOnly date, CancellationToken cancellationToken);

    Task<MenuMutationResult> CreateMenuAsync(Guid tenantId, CreateMenuRequest request, CancellationToken cancellationToken);

    Task<MenuMutationResult> UpdateMenuAsync(Guid tenantId, Guid menuId, UpdateMenuRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes the menu with all of its categories and items. A tenant's last menu cannot be deleted.</summary>
    Task<MenuMutationResult> DeleteMenuAsync(Guid tenantId, Guid menuId, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the menu with all of its categories and items into a new menu, in one save. Nothing is copied
    /// when the copy would take the tenant past the menu, category or item limit.
    /// </summary>
    Task<MenuCloneResult> CloneMenuAsync(Guid tenantId, Guid sourceMenuId, CloneMenuRequest request, CancellationToken cancellationToken);

    Task<bool> SetMenuSettingsAsync(Guid tenantId, bool allowMenuPdfDownload, CancellationToken cancellationToken);

    Task<MenuMutationResult> CreateMenuCategoryAsync(Guid tenantId, CreateMenuCategoryRequest request, CancellationToken cancellationToken);

    /// <summary>A null <paramref name="menuId"/> keeps the category in its current menu.</summary>
    Task<MenuMutationResult> UpdateMenuCategoryAsync(Guid tenantId, Guid categoryId, string name, string description, int sortOrder, Guid? menuId, CancellationToken cancellationToken);

    Task<bool> DeleteMenuCategoryAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);

    Task<MenuItemMutationResult> CreateMenuItemAsync(Guid tenantId, CreateMenuItemRequest request, CancellationToken cancellationToken);

    Task<MenuItemMutationResult> UpdateMenuItemAsync(Guid tenantId, Guid itemId, UpdateMenuItemRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteMenuItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>Sets the page background image URL, which branding updates never touch. Null removes it.</summary>
    Task<bool> SetCustomBackgroundImageUrlAsync(Guid tenantId, string? url, CancellationToken cancellationToken);

    /// <summary>
    /// Sets a menu's background image URL, which menu updates never touch. Null removes it. The previous image is
    /// left in storage (an agent change can still be rolled back to it).
    /// </summary>
    Task<bool> SetMenuBackgroundImageUrlAsync(Guid tenantId, Guid menuId, string? url, CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles the current DB state back to a previously captured snapshot (used for agent-change rollback).
    /// <paramref name="presentKeys"/> holds the top-level property names found in the stored snapshot JSON
    /// (compared ignoring case): a setting added after the snapshot was taken is missing from it and is left
    /// as is, instead of being reset to its default. Null means every property is present.
    /// </summary>
    Task RestoreSnapshotAsync(Guid tenantId, PublicRestaurantResponse snapshot, IReadOnlySet<string>? presentKeys, CancellationToken cancellationToken);
}

public class RestaurantAdminService(AppDbContext dbContext) : IRestaurantAdminService
{
    public async Task<PublicRestaurantResponse> GetSnapshotAsync(Guid tenantId, CancellationToken cancellationToken)
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

        var menus = await dbContext.Menus
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

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

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var allHours = await dbContext.BusinessHours
            .Where(x => x.TenantId == tenantId && (x.Date == null || x.Date >= today))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var hours = allHours
            .Where(x => x.Date == null)
            .OrderBy(x => x.DayOfWeek)
            .ToList();

        var dateSpecificHours = allHours
            .Where(x => x.Date != null)
            .OrderBy(x => x.Date)
            .Select(x => new DateSpecificHourSnapshot(
                x.Date!.Value,
                x.OpenTime.ToString("HH:mm"),
                x.CloseTime.ToString("HH:mm"),
                x.IsClosed))
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

        return new PublicRestaurantResponse(
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
            hours.Select(hour => new BusinessHourResponse(
                hour.DayOfWeek,
                hour.OpenTime.ToString("HH:mm"),
                hour.CloseTime.ToString("HH:mm"),
                hour.IsClosed)).ToList(),
                dateSpecificHours,
                profile.UseLegacyHoursStyle,
                profile.ShowBottomHoursSection,
                profile.UseLegacyBottomHoursStyle,
                profile.ShowCustomBackground,
                profile.CustomBackgroundHexColor1,
                profile.CustomBackgroundHexColor2,
                profile.CustomBackgroundPosition1X,
                profile.CustomBackgroundPosition1Y,
                profile.CustomBackgroundPosition2X,
                profile.CustomBackgroundPosition2Y,
                profile.CustomBackgroundMainHexColor,
                profile.CustomBackgroundMainColorActive,
                profile.CustomBackgroundSplash1Active,
                profile.CustomBackgroundSplash2Active,
                profile.CustomBackgroundImageUrl,
                profile.CustomBackgroundImageActive,
                ShowTopHoursSection: profile.ShowTopHoursSection,
                TopHoursStyle: profile.TopHoursStyle,
                TopHoursOpenStatusDetail: profile.TopHoursOpenStatusDetail,
                Menus: menus
                    .Select(PublicMenuResponse.From)
                    .ToList(),
                AllowMenuPdfDownload: profile.AllowMenuPdfDownload,
                ShowTopContactSection: profile.ShowTopContactSection,
                ShowBottomContactSection: profile.ShowBottomContactSection,
                ContactHoursWhiteBackground: profile.ContactHoursWhiteBackground,
                ContactHoursWhiteBackgroundTransparency: profile.ContactHoursWhiteBackgroundTransparency,
                ApplyTransparencyToMenuItems: profile.ApplyTransparencyToMenuItems,
                HideHeroImage: profile.HideHeroImage,
                PrimaryCtaText: profile.PrimaryCtaText);
    }

    public async Task<bool> UpdateBrandingAsync(Guid tenantId, UpdateBrandingRequest request, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (profile is null)
        {
            return false;
        }

        profile.DisplayName = request.DisplayName;
        profile.Tagline = request.Tagline;
        profile.Announcement = request.Announcement;
        profile.PrimaryHexColor = request.PrimaryHexColor;
        profile.SecondaryHexColor = request.SecondaryHexColor;
        profile.LogoUrl = request.LogoUrl;
        profile.HeroImageUrl = request.HeroImageUrl;
        profile.PrimaryCtaUrl = request.PrimaryCtaUrl;
        profile.PrimaryCtaText = request.PrimaryCtaText;
        profile.Address = request.Address;
        profile.PhoneNumber = request.PhoneNumber;
        profile.ShowCustomBackground = request.ShowCustomBackground;
        profile.CustomBackgroundHexColor1 = request.CustomBackgroundHexColor1;
        profile.CustomBackgroundHexColor2 = request.CustomBackgroundHexColor2;
        profile.CustomBackgroundPosition1X = request.CustomBackgroundPosition1X;
        profile.CustomBackgroundPosition1Y = request.CustomBackgroundPosition1Y;
        profile.CustomBackgroundPosition2X = request.CustomBackgroundPosition2X;
        profile.CustomBackgroundPosition2Y = request.CustomBackgroundPosition2Y;
        profile.CustomBackgroundMainHexColor = request.CustomBackgroundMainHexColor;
        profile.CustomBackgroundMainColorActive = request.CustomBackgroundMainColorActive;
        profile.CustomBackgroundSplash1Active = request.CustomBackgroundSplash1Active;
        profile.CustomBackgroundSplash2Active = request.CustomBackgroundSplash2Active;
        profile.CustomBackgroundImageActive = request.CustomBackgroundImageActive;
        profile.ShowTopContactSection = request.ShowTopContactSection;
        profile.ShowBottomContactSection = request.ShowBottomContactSection;
        profile.ContactHoursWhiteBackground = request.ContactHoursWhiteBackground;
        profile.ContactHoursWhiteBackgroundTransparency = request.ContactHoursWhiteBackgroundTransparency;
        profile.ApplyTransparencyToMenuItems = request.ApplyTransparencyToMenuItems;
        profile.HideHeroImage = request.HideHeroImage;
        profile.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpsertHoursAsync(Guid tenantId, IReadOnlyList<UpsertBusinessHourRequest> request, CancellationToken cancellationToken)
    {
        var existing = await dbContext.BusinessHours
            .Where(x => x.TenantId == tenantId && x.Date == null)
            .ToListAsync(cancellationToken);

        foreach (var hour in request)
        {
            var target = existing.FirstOrDefault(x => x.DayOfWeek == hour.DayOfWeek);
            if (target is null)
            {
                dbContext.BusinessHours.Add(new BusinessHour
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
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
    }

    public async Task<bool> SetTopHoursSectionAsync(Guid tenantId, SetTopHoursSectionRequest request, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (profile is null)
        {
            return false;
        }

        profile.ShowTopHoursSection = request.ShowTopHoursSection;
        profile.TopHoursStyle = request.TopHoursStyle;
        profile.TopHoursOpenStatusDetail = request.TopHoursOpenStatusDetail;
        profile.UseLegacyHoursStyle = request.UseLegacyHoursStyle;
        profile.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetBottomHoursSectionAsync(Guid tenantId, bool showBottomHoursSection, bool useLegacyBottomHoursStyle, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (profile is null)
        {
            return false;
        }

        profile.ShowBottomHoursSection = showBottomHoursSection;
        profile.UseLegacyBottomHoursStyle = useLegacyBottomHoursStyle;
        profile.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpsertDateSpecificHoursAsync(Guid tenantId, IReadOnlyList<UpsertDateSpecificHourRequest> request, CancellationToken cancellationToken)
    {
        var dates = request.Select(x => x.Date).ToList();
        var existing = await dbContext.BusinessHours
            .Where(x => x.TenantId == tenantId && x.Date != null && dates.Contains(x.Date!.Value))
            .ToListAsync(cancellationToken);

        foreach (var item in request)
        {
            var target = existing.FirstOrDefault(x => x.Date == item.Date);
            if (target is null)
            {
                dbContext.BusinessHours.Add(new BusinessHour
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DayOfWeek = item.Date.DayOfWeek,
                    OpenTime = item.OpenTime,
                    CloseTime = item.CloseTime,
                    IsClosed = item.IsClosed,
                    Date = item.Date
                });
            }
            else
            {
                target.DayOfWeek = item.Date.DayOfWeek;
                target.OpenTime = item.OpenTime;
                target.CloseTime = item.CloseTime;
                target.IsClosed = item.IsClosed;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteDateSpecificHourAsync(Guid tenantId, DateOnly date, CancellationToken cancellationToken)
    {
        var hour = await dbContext.BusinessHours
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Date == date, cancellationToken);
        if (hour is null)
        {
            return false;
        }

        dbContext.BusinessHours.Remove(hour);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MenuMutationResult> CreateMenuAsync(Guid tenantId, CreateMenuRequest request, CancellationToken cancellationToken)
    {
        var menuCount = await dbContext.Menus.CountAsync(x => x.TenantId == tenantId, cancellationToken);
        if (menuCount >= MenuLimits.MaxMenusPerTenant)
        {
            return new MenuMutationResult(MenuMutationStatus.LimitReached);
        }

        if (await MenuNameTakenAsync(tenantId, request.Name, excludeMenuId: null, cancellationToken))
        {
            return new MenuMutationResult(MenuMutationStatus.DuplicateName);
        }

        var menu = new Menu
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = request.Name,
            Description = request.Description?.Trim() ?? string.Empty,
            SortOrder = request.SortOrder,
            IsVisible = request.IsVisible
        };

        dbContext.Menus.Add(menu);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new MenuMutationResult(MenuMutationStatus.Success, menu.Id);
    }

    public async Task<MenuMutationResult> UpdateMenuAsync(Guid tenantId, Guid menuId, UpdateMenuRequest request, CancellationToken cancellationToken)
    {
        var menu = await dbContext.Menus
            .SingleOrDefaultAsync(x => x.Id == menuId && x.TenantId == tenantId, cancellationToken);

        if (menu is null)
        {
            return new MenuMutationResult(MenuMutationStatus.NotFound);
        }

        if (await MenuNameTakenAsync(tenantId, request.Name, excludeMenuId: menuId, cancellationToken))
        {
            return new MenuMutationResult(MenuMutationStatus.DuplicateName, menuId);
        }

        menu.Name = request.Name;
        menu.Description = request.Description.Trim();
        menu.SortOrder = request.SortOrder;
        menu.IsVisible = request.IsVisible;
        menu.BackgroundStyle = request.BackgroundStyle ?? menu.BackgroundStyle;
        menu.BackgroundHexColor = request.BackgroundHexColor ?? menu.BackgroundHexColor;
        menu.BackgroundPattern = request.BackgroundPattern ?? menu.BackgroundPattern;
        menu.ShowBackgroundOnSite = request.ShowBackgroundOnSite ?? menu.ShowBackgroundOnSite;
        menu.TextShadowEnabled = request.TextShadowEnabled ?? menu.TextShadowEnabled;
        menu.TextShadowOffsetX = request.TextShadowOffsetX ?? menu.TextShadowOffsetX;
        menu.TextShadowOffsetY = request.TextShadowOffsetY ?? menu.TextShadowOffsetY;
        menu.TextShadowBlur = request.TextShadowBlur ?? menu.TextShadowBlur;
        menu.TextShadowHexColor = request.TextShadowHexColor ?? menu.TextShadowHexColor;
        menu.ShowTextShadowOnSite = request.ShowTextShadowOnSite ?? menu.ShowTextShadowOnSite;

        await dbContext.SaveChangesAsync(cancellationToken);
        return new MenuMutationResult(MenuMutationStatus.Success, menuId);
    }

    public async Task<MenuMutationResult> DeleteMenuAsync(Guid tenantId, Guid menuId, CancellationToken cancellationToken)
    {
        var menu = await dbContext.Menus
            .SingleOrDefaultAsync(x => x.Id == menuId && x.TenantId == tenantId, cancellationToken);

        if (menu is null)
        {
            return new MenuMutationResult(MenuMutationStatus.NotFound);
        }

        // Every category must belong to a menu, so the last menu stays.
        var menuCount = await dbContext.Menus.CountAsync(x => x.TenantId == tenantId, cancellationToken);
        if (menuCount <= 1)
        {
            return new MenuMutationResult(MenuMutationStatus.LastMenu, menuId);
        }

        var categories = await dbContext.MenuCategories
            .Where(x => x.TenantId == tenantId && x.MenuId == menuId)
            .ToListAsync(cancellationToken);
        var categoryIds = categories.Select(x => x.Id).ToList();
        var items = await dbContext.MenuItems
            .Where(x => x.TenantId == tenantId && categoryIds.Contains(x.CategoryId))
            .ToListAsync(cancellationToken);

        dbContext.MenuItems.RemoveRange(items);
        dbContext.MenuCategories.RemoveRange(categories);
        dbContext.Menus.Remove(menu);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new MenuMutationResult(MenuMutationStatus.Success, menuId);
    }

    public async Task<MenuCloneResult> CloneMenuAsync(Guid tenantId, Guid sourceMenuId, CloneMenuRequest request, CancellationToken cancellationToken)
    {
        var source = await dbContext.Menus
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sourceMenuId && x.TenantId == tenantId, cancellationToken);

        if (source is null)
        {
            return new MenuCloneResult(MenuCloneStatus.NotFound);
        }

        var sourceCategories = await dbContext.MenuCategories
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.MenuId == sourceMenuId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var sourceCategoryIds = sourceCategories.Select(x => x.Id).ToList();
        var sourceItems = await dbContext.MenuItems
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && sourceCategoryIds.Contains(x.CategoryId))
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var menus = await dbContext.Menus
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Name, x.SortOrder })
            .ToListAsync(cancellationToken);
        var categoryCount = await dbContext.MenuCategories.CountAsync(x => x.TenantId == tenantId, cancellationToken);
        var itemCount = await dbContext.MenuItems.CountAsync(x => x.TenantId == tenantId, cancellationToken);

        var limitProblems = MenuCloneRules.LimitProblems(
            menus.Count, categoryCount, itemCount, sourceCategories.Count, sourceItems.Count);
        if (limitProblems.Count > 0)
        {
            return new MenuCloneResult(MenuCloneStatus.LimitReached, source.Name, LimitProblems: limitProblems);
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? MenuCloneRules.NextCopyName(source.Name, menus.Select(x => x.Name))
            : request.Name.Trim();

        // Same comparison as MenuNameTakenAsync and the menus (tenant_id, name) unique constraint.
        if (menus.Any(x => x.Name == name))
        {
            return new MenuCloneResult(MenuCloneStatus.DuplicateName, source.Name);
        }

        var menu = new Menu
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Description = request.Description?.Trim() ?? source.Description,
            SortOrder = request.SortOrder ?? menus.Max(x => x.SortOrder) + 1,
            // A fresh copy starts hidden so a duplicate never shows up on the live site before the tenant is ready.
            IsVisible = request.IsVisible ?? false,
            // The copy shares the source's background image URL. Each upload has its own R2 key and an image is
            // only deleted from R2 once no menu uses it, so either menu can replace or delete it independently.
            BackgroundStyle = source.BackgroundStyle,
            BackgroundImageUrl = source.BackgroundImageUrl,
            BackgroundHexColor = source.BackgroundHexColor,
            BackgroundPattern = source.BackgroundPattern,
            ShowBackgroundOnSite = source.ShowBackgroundOnSite,
            TextShadowEnabled = source.TextShadowEnabled,
            TextShadowOffsetX = source.TextShadowOffsetX,
            TextShadowOffsetY = source.TextShadowOffsetY,
            TextShadowBlur = source.TextShadowBlur,
            TextShadowHexColor = source.TextShadowHexColor,
            ShowTextShadowOnSite = source.ShowTextShadowOnSite
        };
        dbContext.Menus.Add(menu);

        var itemsByCategory = sourceItems.ToLookup(x => x.CategoryId);
        var categoryResponses = new List<PublicMenuCategoryResponse>();
        foreach (var sourceCategory in sourceCategories)
        {
            var category = new MenuCategory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MenuId = menu.Id,
                Name = sourceCategory.Name,
                Description = sourceCategory.Description,
                SortOrder = sourceCategory.SortOrder
            };
            dbContext.MenuCategories.Add(category);

            var itemResponses = new List<PublicMenuItemResponse>();
            foreach (var sourceItem in itemsByCategory[sourceCategory.Id])
            {
                var item = new MenuItem
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CategoryId = category.Id,
                    Name = sourceItem.Name,
                    Description = sourceItem.Description,
                    Price = sourceItem.Price,
                    IsAvailable = sourceItem.IsAvailable,
                    SortOrder = sourceItem.SortOrder
                };
                dbContext.MenuItems.Add(item);
                itemResponses.Add(new PublicMenuItemResponse(
                    item.Id, item.Name, item.Description, item.Price, item.IsAvailable, item.SortOrder));
            }

            categoryResponses.Add(new PublicMenuCategoryResponse(
                category.Id, category.Name, category.Description, category.SortOrder, itemResponses, menu.Id));
        }

        // One save, so a failure part-way never leaves a half-copied menu behind.
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MenuCloneResult(
            MenuCloneStatus.Success,
            source.Name,
            new ClonedMenuResponse(
                PublicMenuResponse.From(menu),
                categoryResponses));
    }

    public async Task<bool> SetMenuSettingsAsync(Guid tenantId, bool allowMenuPdfDownload, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (profile is null)
        {
            return false;
        }

        profile.AllowMenuPdfDownload = allowMenuPdfDownload;
        profile.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MenuMutationResult> CreateMenuCategoryAsync(Guid tenantId, CreateMenuCategoryRequest request, CancellationToken cancellationToken)
    {
        var categoryCount = await dbContext.MenuCategories.CountAsync(x => x.TenantId == tenantId, cancellationToken);
        if (categoryCount >= MenuLimits.MaxCategoriesPerTenant)
        {
            return new MenuMutationResult(MenuMutationStatus.LimitReached);
        }

        Guid menuId;
        if (request.MenuId is { } requestedMenuId)
        {
            if (!await MenuExistsAsync(tenantId, requestedMenuId, cancellationToken))
            {
                return new MenuMutationResult(MenuMutationStatus.MenuInvalid);
            }

            menuId = requestedMenuId;
        }
        else
        {
            menuId = await GetOrCreateDefaultMenuIdAsync(tenantId, cancellationToken);
        }

        if (await CategoryNameTakenAsync(tenantId, menuId, request.Name, excludeCategoryId: null, cancellationToken))
        {
            return new MenuMutationResult(MenuMutationStatus.DuplicateName);
        }

        var category = new MenuCategory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MenuId = menuId,
            Name = request.Name,
            Description = request.Description?.Trim() ?? string.Empty,
            SortOrder = request.SortOrder
        };

        dbContext.MenuCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new MenuMutationResult(MenuMutationStatus.Success, category.Id);
    }

    public async Task<MenuMutationResult> UpdateMenuCategoryAsync(Guid tenantId, Guid categoryId, string name, string description, int sortOrder, Guid? menuId, CancellationToken cancellationToken)
    {
        var category = await dbContext.MenuCategories
            .SingleOrDefaultAsync(x => x.Id == categoryId && x.TenantId == tenantId, cancellationToken);

        if (category is null)
        {
            return new MenuMutationResult(MenuMutationStatus.NotFound);
        }

        var targetMenuId = menuId ?? category.MenuId;
        if (targetMenuId != category.MenuId && !await MenuExistsAsync(tenantId, targetMenuId, cancellationToken))
        {
            return new MenuMutationResult(MenuMutationStatus.MenuInvalid, categoryId);
        }

        if (await CategoryNameTakenAsync(tenantId, targetMenuId, name, excludeCategoryId: categoryId, cancellationToken))
        {
            return new MenuMutationResult(MenuMutationStatus.DuplicateName, categoryId);
        }

        category.MenuId = targetMenuId;
        category.Name = name;
        category.Description = description.Trim();
        category.SortOrder = sortOrder;

        await dbContext.SaveChangesAsync(cancellationToken);
        return new MenuMutationResult(MenuMutationStatus.Success, categoryId);
    }

    private Task<bool> MenuExistsAsync(Guid tenantId, Guid menuId, CancellationToken cancellationToken) =>
        dbContext.Menus.AnyAsync(x => x.Id == menuId && x.TenantId == tenantId, cancellationToken);

    private Task<bool> MenuNameTakenAsync(Guid tenantId, string name, Guid? excludeMenuId, CancellationToken cancellationToken) =>
        dbContext.Menus.AnyAsync(
            x => x.TenantId == tenantId && x.Name == name && (excludeMenuId == null || x.Id != excludeMenuId),
            cancellationToken);

    private Task<bool> CategoryNameTakenAsync(Guid tenantId, Guid menuId, string name, Guid? excludeCategoryId, CancellationToken cancellationToken) =>
        dbContext.MenuCategories.AnyAsync(
            x => x.TenantId == tenantId && x.MenuId == menuId && x.Name == name && (excludeCategoryId == null || x.Id != excludeCategoryId),
            cancellationToken);

    /// <summary>
    /// The tenant's first menu by sort order, used when a caller (e.g. the agent pipeline) doesn't
    /// pick one. Creates and saves the default "Menu" if the tenant somehow has none.
    /// </summary>
    private async Task<Guid> GetOrCreateDefaultMenuIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var existingMenuId = await dbContext.Menus
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingMenuId is { } menuId)
        {
            return menuId;
        }

        var menu = new Menu
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = Menu.DefaultName
        };

        dbContext.Menus.Add(menu);
        await dbContext.SaveChangesAsync(cancellationToken);
        return menu.Id;
    }

    public async Task<bool> DeleteMenuCategoryAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await dbContext.MenuCategories
            .SingleOrDefaultAsync(x => x.Id == categoryId && x.TenantId == tenantId, cancellationToken);

        if (category is null)
        {
            return false;
        }

        var items = await dbContext.MenuItems
            .Where(x => x.TenantId == tenantId && x.CategoryId == categoryId)
            .ToListAsync(cancellationToken);

        dbContext.MenuItems.RemoveRange(items);
        dbContext.MenuCategories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MenuItemMutationResult> CreateMenuItemAsync(Guid tenantId, CreateMenuItemRequest request, CancellationToken cancellationToken)
    {
        var categoryExists = await dbContext.MenuCategories
            .AnyAsync(x => x.Id == request.CategoryId && x.TenantId == tenantId, cancellationToken);

        if (!categoryExists)
        {
            return new MenuItemMutationResult(MenuItemMutationStatus.CategoryInvalid);
        }

        var itemCount = await dbContext.MenuItems.CountAsync(x => x.TenantId == tenantId, cancellationToken);
        if (itemCount >= MenuLimits.MaxItemsPerTenant)
        {
            return new MenuItemMutationResult(MenuItemMutationStatus.LimitReached);
        }

        var item = new MenuItem
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CategoryId = request.CategoryId,
            Name = request.Name,
            Description = request.Description,
            Price = request.Price,
            IsAvailable = request.IsAvailable,
            SortOrder = request.SortOrder
        };

        dbContext.MenuItems.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new MenuItemMutationResult(MenuItemMutationStatus.Success, item.Id);
    }

    public async Task<MenuItemMutationResult> UpdateMenuItemAsync(Guid tenantId, Guid itemId, UpdateMenuItemRequest request, CancellationToken cancellationToken)
    {
        var item = await dbContext.MenuItems
            .SingleOrDefaultAsync(x => x.Id == itemId && x.TenantId == tenantId, cancellationToken);

        if (item is null)
        {
            return new MenuItemMutationResult(MenuItemMutationStatus.NotFound);
        }

        var categoryExists = await dbContext.MenuCategories
            .AnyAsync(x => x.Id == request.CategoryId && x.TenantId == tenantId, cancellationToken);

        if (!categoryExists)
        {
            return new MenuItemMutationResult(MenuItemMutationStatus.CategoryInvalid, itemId);
        }

        item.CategoryId = request.CategoryId;
        item.Name = request.Name;
        item.Description = request.Description;
        item.Price = request.Price;
        item.IsAvailable = request.IsAvailable;
        item.SortOrder = request.SortOrder;

        await dbContext.SaveChangesAsync(cancellationToken);
        return new MenuItemMutationResult(MenuItemMutationStatus.Success, itemId);
    }

    public async Task<bool> DeleteMenuItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await dbContext.MenuItems
            .SingleOrDefaultAsync(x => x.Id == itemId && x.TenantId == tenantId, cancellationToken);

        if (item is null)
        {
            return false;
        }

        dbContext.MenuItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetCustomBackgroundImageUrlAsync(Guid tenantId, string? url, CancellationToken cancellationToken)
    {
        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (profile is null)
        {
            return false;
        }

        profile.CustomBackgroundImageUrl = url;
        profile.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetMenuBackgroundImageUrlAsync(Guid tenantId, Guid menuId, string? url, CancellationToken cancellationToken)
    {
        var menu = await dbContext.Menus
            .SingleOrDefaultAsync(x => x.Id == menuId && x.TenantId == tenantId, cancellationToken);

        if (menu is null)
        {
            return false;
        }

        menu.BackgroundImageUrl = url;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task RestoreSnapshotAsync(Guid tenantId, PublicRestaurantResponse snapshot, IReadOnlySet<string>? presentKeys, CancellationToken cancellationToken)
    {
        // A snapshot stored before a setting existed has no key for it; deserializing fills in the
        // default, which must not overwrite whatever the tenant has set since.
        bool Has(string propertyName) => presentKeys is null || presentKeys.Contains(propertyName);

        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (profile is not null)
        {
            profile.DisplayName = snapshot.DisplayName;
            profile.Tagline = snapshot.Tagline;
            profile.Announcement = snapshot.Announcement;
            profile.PrimaryHexColor = snapshot.PrimaryHexColor;
            profile.SecondaryHexColor = snapshot.SecondaryHexColor;
            profile.LogoUrl = snapshot.LogoUrl;
            profile.HeroImageUrl = snapshot.HeroImageUrl;
            profile.PrimaryCtaUrl = BrandingRules.RestorableCtaUrl(snapshot.PrimaryCtaUrl);
            profile.Address = snapshot.Address;
            profile.PhoneNumber = snapshot.PhoneNumber;
            if (Has(nameof(snapshot.PrimaryCtaText))) profile.PrimaryCtaText = snapshot.PrimaryCtaText;

            if (Has(nameof(snapshot.UseLegacyHoursStyle))) profile.UseLegacyHoursStyle = snapshot.UseLegacyHoursStyle;
            if (Has(nameof(snapshot.ShowTopHoursSection))) profile.ShowTopHoursSection = snapshot.ShowTopHoursSection;
            if (Has(nameof(snapshot.TopHoursStyle))) profile.TopHoursStyle = snapshot.TopHoursStyle;
            if (Has(nameof(snapshot.TopHoursOpenStatusDetail))) profile.TopHoursOpenStatusDetail = snapshot.TopHoursOpenStatusDetail;
            if (Has(nameof(snapshot.ShowBottomHoursSection))) profile.ShowBottomHoursSection = snapshot.ShowBottomHoursSection;
            if (Has(nameof(snapshot.UseLegacyBottomHoursStyle))) profile.UseLegacyBottomHoursStyle = snapshot.UseLegacyBottomHoursStyle;

            if (Has(nameof(snapshot.ShowCustomBackground))) profile.ShowCustomBackground = snapshot.ShowCustomBackground;
            if (Has(nameof(snapshot.CustomBackgroundHexColor1))) profile.CustomBackgroundHexColor1 = snapshot.CustomBackgroundHexColor1;
            if (Has(nameof(snapshot.CustomBackgroundHexColor2))) profile.CustomBackgroundHexColor2 = snapshot.CustomBackgroundHexColor2;
            if (Has(nameof(snapshot.CustomBackgroundPosition1X))) profile.CustomBackgroundPosition1X = snapshot.CustomBackgroundPosition1X;
            if (Has(nameof(snapshot.CustomBackgroundPosition1Y))) profile.CustomBackgroundPosition1Y = snapshot.CustomBackgroundPosition1Y;
            if (Has(nameof(snapshot.CustomBackgroundPosition2X))) profile.CustomBackgroundPosition2X = snapshot.CustomBackgroundPosition2X;
            if (Has(nameof(snapshot.CustomBackgroundPosition2Y))) profile.CustomBackgroundPosition2Y = snapshot.CustomBackgroundPosition2Y;
            if (Has(nameof(snapshot.CustomBackgroundMainHexColor))) profile.CustomBackgroundMainHexColor = snapshot.CustomBackgroundMainHexColor;
            if (Has(nameof(snapshot.CustomBackgroundMainColorActive))) profile.CustomBackgroundMainColorActive = snapshot.CustomBackgroundMainColorActive;
            if (Has(nameof(snapshot.CustomBackgroundSplash1Active))) profile.CustomBackgroundSplash1Active = snapshot.CustomBackgroundSplash1Active;
            if (Has(nameof(snapshot.CustomBackgroundSplash2Active))) profile.CustomBackgroundSplash2Active = snapshot.CustomBackgroundSplash2Active;
            if (Has(nameof(snapshot.CustomBackgroundImageUrl))) profile.CustomBackgroundImageUrl = snapshot.CustomBackgroundImageUrl;
            if (Has(nameof(snapshot.CustomBackgroundImageActive))) profile.CustomBackgroundImageActive = snapshot.CustomBackgroundImageActive;

            if (Has(nameof(snapshot.AllowMenuPdfDownload))) profile.AllowMenuPdfDownload = snapshot.AllowMenuPdfDownload;
            if (Has(nameof(snapshot.ShowTopContactSection))) profile.ShowTopContactSection = snapshot.ShowTopContactSection;
            if (Has(nameof(snapshot.ShowBottomContactSection))) profile.ShowBottomContactSection = snapshot.ShowBottomContactSection;
            if (Has(nameof(snapshot.ContactHoursWhiteBackground))) profile.ContactHoursWhiteBackground = snapshot.ContactHoursWhiteBackground;
            if (Has(nameof(snapshot.ContactHoursWhiteBackgroundTransparency))) profile.ContactHoursWhiteBackgroundTransparency = snapshot.ContactHoursWhiteBackgroundTransparency;
            if (Has(nameof(snapshot.ApplyTransparencyToMenuItems))) profile.ApplyTransparencyToMenuItems = snapshot.ApplyTransparencyToMenuItems;
            if (Has(nameof(snapshot.HideHeroImage))) profile.HideHeroImage = snapshot.HideHeroImage;

            profile.UpdatedUtc = DateTime.UtcNow;
        }

        var existingRecurringHours = await dbContext.BusinessHours
            .Where(x => x.TenantId == tenantId && x.Date == null)
            .ToListAsync(cancellationToken);

        foreach (var hour in snapshot.Hours)
        {
            var openTime = TimeOnly.Parse(hour.OpenTime);
            var closeTime = TimeOnly.Parse(hour.CloseTime);
            var target = existingRecurringHours.FirstOrDefault(x => x.DayOfWeek == hour.DayOfWeek);

            if (target is null)
            {
                dbContext.BusinessHours.Add(new BusinessHour
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DayOfWeek = hour.DayOfWeek,
                    OpenTime = openTime,
                    CloseTime = closeTime,
                    IsClosed = hour.IsClosed
                });
            }
            else
            {
                target.OpenTime = openTime;
                target.CloseTime = closeTime;
                target.IsClosed = hour.IsClosed;
            }
        }

        // Null means the snapshot predates date-specific hours, so leave overrides untouched.
        if (snapshot.DateSpecificHours is not null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var existingOverrides = await dbContext.BusinessHours
                .Where(x => x.TenantId == tenantId && x.Date != null && x.Date >= today)
                .ToListAsync(cancellationToken);

            var snapshotDates = snapshot.DateSpecificHours.Select(x => x.Date).ToHashSet();
            dbContext.BusinessHours.RemoveRange(existingOverrides.Where(x => !snapshotDates.Contains(x.Date!.Value)));

            foreach (var entry in snapshot.DateSpecificHours.Where(x => x.Date >= today))
            {
                var openTime = TimeOnly.Parse(entry.OpenTime);
                var closeTime = TimeOnly.Parse(entry.CloseTime);
                var target = existingOverrides.FirstOrDefault(x => x.Date == entry.Date);

                if (target is null)
                {
                    dbContext.BusinessHours.Add(new BusinessHour
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        DayOfWeek = entry.Date.DayOfWeek,
                        OpenTime = openTime,
                        CloseTime = closeTime,
                        IsClosed = entry.IsClosed,
                        Date = entry.Date
                    });
                }
                else
                {
                    target.OpenTime = openTime;
                    target.CloseTime = closeTime;
                    target.IsClosed = entry.IsClosed;
                }
            }
        }

        var existingMenus = await dbContext.Menus
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var existingCategories = await dbContext.MenuCategories
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var existingItems = await dbContext.MenuItems
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var snapshotMenus = Has(nameof(snapshot.Menus)) && snapshot.Menus is { Count: > 0 }
            ? snapshot.Menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Name).ToList()
            : null;
        var snapshotMenuIds = snapshotMenus?.Select(m => m.Id).ToHashSet();
        var existingMenuIds = existingMenus.Select(m => m.Id).ToHashSet();
        Guid? defaultMenuId = null;

        async Task<Guid?> ResolveMenuIdAsync(Guid? snapshotMenuId, bool categoryExists)
        {
            if (snapshotMenus is not null)
            {
                return snapshotMenuId is { } id && snapshotMenuIds!.Contains(id) ? id : snapshotMenus[0].Id;
            }

            if (snapshotMenuId is { } existingId && existingMenuIds.Contains(existingId))
            {
                return existingId;
            }

            // An existing category stays in the menu it is in; a re-created one needs some menu.
            return categoryExists ? null : defaultMenuId ??= await GetOrCreateDefaultMenuIdAsync(tenantId, cancellationToken);
        }

        var snapshotCategoryIds = snapshot.Menu.Select(c => c.Id).ToHashSet();
        var snapshotItemIds = snapshot.Menu.SelectMany(c => c.Items).Select(i => i.Id).ToHashSet();

        var staleCategories = existingCategories.Where(c => !snapshotCategoryIds.Contains(c.Id)).ToList();
        var staleMenus = snapshotMenus is null
            ? new List<Menu>()
            : existingMenus.Where(m => !snapshotMenuIds!.Contains(m.Id)).ToList();

        // Step 1: remove items created after the snapshot, and move every menu/category that is about to be
        // renamed, moved or deleted onto a placeholder name. Menu names are unique per tenant and category
        // names per menu, so restoring names directly could collide (e.g. two menus that swapped names).
        dbContext.MenuItems.RemoveRange(existingItems.Where(i => !snapshotItemIds.Contains(i.Id)));

        static string PlaceholderName(Guid id) => $"__restore_{id:N}";

        foreach (var menu in staleMenus)
        {
            menu.Name = PlaceholderName(menu.Id);
        }

        if (snapshotMenus is not null)
        {
            foreach (var menu in existingMenus)
            {
                var snapshotMenu = snapshotMenus.FirstOrDefault(m => m.Id == menu.Id);
                if (snapshotMenu is not null && snapshotMenu.Name != menu.Name)
                {
                    menu.Name = PlaceholderName(menu.Id);
                }
            }
        }

        foreach (var category in staleCategories)
        {
            category.Name = PlaceholderName(category.Id);
        }

        var targetMenuIdByCategory = new Dictionary<Guid, Guid?>();
        foreach (var category in snapshot.Menu)
        {
            var existing = existingCategories.FirstOrDefault(c => c.Id == category.Id);
            var targetMenuId = await ResolveMenuIdAsync(category.MenuId, existing is not null);
            targetMenuIdByCategory[category.Id] = targetMenuId;

            if (existing is not null &&
                (existing.Name != category.Name || (targetMenuId is { } menuId && menuId != existing.MenuId)))
            {
                existing.Name = PlaceholderName(existing.Id);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Step 2: bring back the snapshot's menus, categories and items.
        if (snapshotMenus is not null)
        {
            foreach (var menu in snapshotMenus)
            {
                var target = existingMenus.FirstOrDefault(m => m.Id == menu.Id);
                if (target is null)
                {
                    target = new Menu { Id = menu.Id, TenantId = tenantId };
                    dbContext.Menus.Add(target);
                }

                target.Name = menu.Name;
                target.Description = menu.Description ?? string.Empty;
                target.SortOrder = menu.SortOrder;
                target.IsVisible = menu.IsVisible;
                RestoreMenuBackground(target, menu);
                RestoreMenuTextShadow(target, menu);
            }
        }

        foreach (var category in snapshot.Menu)
        {
            var targetMenuId = targetMenuIdByCategory[category.Id];
            var targetCategory = existingCategories.FirstOrDefault(c => c.Id == category.Id);
            if (targetCategory is null)
            {
                dbContext.MenuCategories.Add(new MenuCategory
                {
                    Id = category.Id,
                    TenantId = tenantId,
                    MenuId = targetMenuId!.Value,
                    Name = category.Name,
                    Description = category.Description,
                    SortOrder = category.SortOrder
                });
            }
            else
            {
                if (targetMenuId is { } menuId)
                {
                    targetCategory.MenuId = menuId;
                }

                targetCategory.Name = category.Name;
                targetCategory.Description = category.Description;
                targetCategory.SortOrder = category.SortOrder;
            }

            foreach (var item in category.Items)
            {
                var targetItem = existingItems.FirstOrDefault(i => i.Id == item.Id);
                if (targetItem is null)
                {
                    dbContext.MenuItems.Add(new MenuItem
                    {
                        Id = item.Id,
                        TenantId = tenantId,
                        CategoryId = category.Id,
                        Name = item.Name,
                        Description = item.Description,
                        Price = item.Price,
                        IsAvailable = item.IsAvailable,
                        SortOrder = item.SortOrder
                    });
                }
                else
                {
                    targetItem.CategoryId = category.Id;
                    targetItem.Name = item.Name;
                    targetItem.Description = item.Description;
                    targetItem.Price = item.Price;
                    targetItem.IsAvailable = item.IsAvailable;
                    targetItem.SortOrder = item.SortOrder;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Step 3: only now remove what was created after the snapshot. Every surviving item and category has
        // been moved back first, so deleting a category or menu can't cascade onto something being restored.
        dbContext.MenuCategories.RemoveRange(staleCategories);
        dbContext.Menus.RemoveRange(staleMenus);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A snapshot stored before menu backgrounds existed has no BackgroundStyle for its menus; the menu's current
    /// background is then left as is instead of being cleared.
    /// </summary>
    private static void RestoreMenuBackground(Menu target, PublicMenuResponse snapshotMenu)
    {
        if (snapshotMenu.BackgroundStyle is null)
        {
            return;
        }

        target.BackgroundStyle = snapshotMenu.BackgroundStyle;
        target.BackgroundImageUrl = snapshotMenu.BackgroundImageUrl;
        target.BackgroundHexColor = snapshotMenu.BackgroundHexColor;
        target.BackgroundPattern = snapshotMenu.BackgroundPattern;
        target.ShowBackgroundOnSite = snapshotMenu.ShowBackgroundOnSite;
    }

    /// <summary>
    /// A snapshot stored before menu text shadows existed has no TextShadowEnabled for its menus; the menu's
    /// current text shadow is then left as is instead of being reset.
    /// </summary>
    private static void RestoreMenuTextShadow(Menu target, PublicMenuResponse snapshotMenu)
    {
        if (snapshotMenu.TextShadowEnabled is not { } enabled)
        {
            return;
        }

        target.TextShadowEnabled = enabled;
        target.TextShadowOffsetX = snapshotMenu.TextShadowOffsetX;
        target.TextShadowOffsetY = snapshotMenu.TextShadowOffsetY;
        target.TextShadowBlur = snapshotMenu.TextShadowBlur;
        target.TextShadowHexColor = snapshotMenu.TextShadowHexColor ?? MenuTextShadowRules.DefaultHexColor;
        target.ShowTextShadowOnSite = snapshotMenu.ShowTextShadowOnSite;
    }
}
