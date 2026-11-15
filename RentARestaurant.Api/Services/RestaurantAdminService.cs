using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
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

    Task<bool> SetMenuSettingsAsync(Guid tenantId, bool allowMenuPdfDownload, CancellationToken cancellationToken);

    Task<MenuMutationResult> CreateMenuCategoryAsync(Guid tenantId, CreateMenuCategoryRequest request, CancellationToken cancellationToken);

    /// <summary>A null <paramref name="menuId"/> keeps the category in its current menu.</summary>
    Task<MenuMutationResult> UpdateMenuCategoryAsync(Guid tenantId, Guid categoryId, string name, string description, int sortOrder, Guid? menuId, CancellationToken cancellationToken);

    Task<bool> DeleteMenuCategoryAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);

    Task<MenuItemMutationResult> CreateMenuItemAsync(Guid tenantId, CreateMenuItemRequest request, CancellationToken cancellationToken);

    Task<MenuItemMutationResult> UpdateMenuItemAsync(Guid tenantId, Guid itemId, UpdateMenuItemRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteMenuItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>Reconciles the current DB state back to a previously captured snapshot (used for agent-change rollback).</summary>
    Task RestoreSnapshotAsync(Guid tenantId, PublicRestaurantResponse snapshot, CancellationToken cancellationToken);
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
                    .Select(m => new PublicMenuResponse(m.Id, m.Name, m.Description, m.SortOrder, m.IsVisible))
                    .ToList(),
                AllowMenuPdfDownload: profile.AllowMenuPdfDownload);
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

    public async Task RestoreSnapshotAsync(Guid tenantId, PublicRestaurantResponse snapshot, CancellationToken cancellationToken)
    {
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
            profile.PrimaryCtaUrl = snapshot.PrimaryCtaUrl;
            profile.Address = snapshot.Address;
            profile.PhoneNumber = snapshot.PhoneNumber;
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

        var existingCategories = await dbContext.MenuCategories
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var existingItems = await dbContext.MenuItems
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Snapshots taken before multiple menus existed carry no MenuId, and a menu may have been deleted
        // since the snapshot - either way the category lands in the tenant's default menu.
        var existingMenuIds = (await dbContext.Menus
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
        Guid? defaultMenuId = null;

        async Task<Guid> ResolveMenuIdAsync(Guid? snapshotMenuId) =>
            snapshotMenuId is { } id && existingMenuIds.Contains(id)
                ? id
                : defaultMenuId ??= await GetOrCreateDefaultMenuIdAsync(tenantId, cancellationToken);

        var snapshotCategoryIds = snapshot.Menu.Select(c => c.Id).ToHashSet();
        var snapshotItemIds = snapshot.Menu.SelectMany(c => c.Items).Select(i => i.Id).ToHashSet();

        // Anything created after the snapshot was taken (i.e. not present in it) gets removed,
        // which correctly reverses agent-driven creates as part of the rollback.
        dbContext.MenuItems.RemoveRange(existingItems.Where(i => !snapshotItemIds.Contains(i.Id)));
        dbContext.MenuCategories.RemoveRange(existingCategories.Where(c => !snapshotCategoryIds.Contains(c.Id)));

        foreach (var category in snapshot.Menu)
        {
            var targetCategory = existingCategories.FirstOrDefault(c => c.Id == category.Id);
            if (targetCategory is null)
            {
                dbContext.MenuCategories.Add(new MenuCategory
                {
                    Id = category.Id,
                    TenantId = tenantId,
                    MenuId = await ResolveMenuIdAsync(category.MenuId),
                    Name = category.Name,
                    Description = category.Description,
                    SortOrder = category.SortOrder
                });
            }
            else
            {
                if (category.MenuId is { } snapshotMenuId && existingMenuIds.Contains(snapshotMenuId))
                {
                    targetCategory.MenuId = snapshotMenuId;
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
    }
}
