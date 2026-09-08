using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Services;

public enum MenuItemMutationStatus
{
    Success,
    NotFound,
    CategoryInvalid
}

public sealed record MenuItemMutationResult(MenuItemMutationStatus Status, Guid? ItemId = null);

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

    Task<Guid> CreateMenuCategoryAsync(Guid tenantId, CreateMenuCategoryRequest request, CancellationToken cancellationToken);

    Task<bool> UpdateMenuCategoryAsync(Guid tenantId, Guid categoryId, string name, int sortOrder, CancellationToken cancellationToken);

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
            .Where(x => x.TenantId == tenantId && (x.Date == null || x.Date == today))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var hours = allHours
            .GroupBy(x => x.DayOfWeek)
            .Select(g => g.FirstOrDefault(x => x.Date == today) ?? g.First(x => x.Date == null))
            .OrderBy(x => x.DayOfWeek)
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
        profile.PrimaryHexColor = request.PrimaryHexColor;
        profile.SecondaryHexColor = request.SecondaryHexColor;
        profile.LogoUrl = request.LogoUrl;
        profile.HeroImageUrl = request.HeroImageUrl;
        profile.PrimaryCtaUrl = request.PrimaryCtaUrl;
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

    public async Task<Guid> CreateMenuCategoryAsync(Guid tenantId, CreateMenuCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = new MenuCategory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = request.Name,
            SortOrder = request.SortOrder
        };

        dbContext.MenuCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        return category.Id;
    }

    public async Task<bool> UpdateMenuCategoryAsync(Guid tenantId, Guid categoryId, string name, int sortOrder, CancellationToken cancellationToken)
    {
        var category = await dbContext.MenuCategories
            .SingleOrDefaultAsync(x => x.Id == categoryId && x.TenantId == tenantId, cancellationToken);

        if (category is null)
        {
            return false;
        }

        category.Name = name;
        category.SortOrder = sortOrder;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
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
            profile.PrimaryHexColor = snapshot.PrimaryHexColor;
            profile.SecondaryHexColor = snapshot.SecondaryHexColor;
            profile.LogoUrl = snapshot.LogoUrl;
            profile.HeroImageUrl = snapshot.HeroImageUrl;
            profile.PrimaryCtaUrl = snapshot.PrimaryCtaUrl;
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

        var existingCategories = await dbContext.MenuCategories
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var existingItems = await dbContext.MenuItems
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken);

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
                    Name = category.Name,
                    SortOrder = category.SortOrder
                });
            }
            else
            {
                targetCategory.Name = category.Name;
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
