using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Domain.Entities;

namespace RentARestaurant.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var hasTenants = await dbContext.Tenants.AnyAsync(cancellationToken);
        if (hasTenants)
        {
            return;
        }

        var tenantId = Guid.NewGuid();

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Beef Store",
            Slug = "beef-store",
            IsActive = true,
            SubscriptionPlan = "Self-Service",
            SubscriptionState = "active"
        };

        dbContext.Tenants.Add(tenant);

        dbContext.RestaurantProfiles.Add(new RestaurantProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = "Beef Store",
            Tagline = "Steak and grill crafted daily",
            PrimaryHexColor = "#7A2A1A",
            SecondaryHexColor = "#F9EFE8",
            PrimaryCtaUrl = "https://example.com/order"
        });

        var mains = new MenuCategory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Mains",
            SortOrder = 1
        };

        dbContext.MenuCategories.Add(mains);

        dbContext.MenuItems.AddRange(
            new MenuItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CategoryId = mains.Id,
                Name = "Ribeye 12oz",
                Description = "Dry-aged ribeye with herb butter",
                Price = 28.50m,
                SortOrder = 1,
                IsAvailable = true
            },
            new MenuItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CategoryId = mains.Id,
                Name = "Classic Burger",
                Description = "Grass-fed beef, cheddar, brioche bun",
                Price = 14.00m,
                SortOrder = 2,
                IsAvailable = true
            }
        );

        for (var i = 0; i < 7; i++)
        {
            dbContext.BusinessHours.Add(new BusinessHour
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DayOfWeek = (DayOfWeek)i,
                OpenTime = new TimeOnly(11, 0),
                CloseTime = new TimeOnly(22, 0),
                IsClosed = i == 1
            });
        }

        dbContext.TenantUsers.Add(new TenantUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ExternalUserId = "owner-demo",
            Email = "owner@beefstore.com",
            Role = "Owner"
        });

        // second tenant for testing multi-tenancy
        var tenantId2 = Guid.NewGuid();

        var tenant2 = new Tenant
        {
            Id = tenantId2,
            Name = "Wendy's",
            Slug = "wendys",
            IsActive = true,
            SubscriptionPlan = "Done-For-You",
            SubscriptionState = "active"
        };

        dbContext.Tenants.Add(tenant2);

        dbContext.RestaurantProfiles.Add(new RestaurantProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId2,
            DisplayName = "Wendy's",
            Tagline = "Freshly made fast food",
            PrimaryHexColor = "#FF0000",
            SecondaryHexColor = "#FFFFFF",
            PrimaryCtaUrl = "https://www.wendys.com/order"
        });

        var mains2 = new MenuCategory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId2,
            Name = "Mains",
            SortOrder = 1
        };

        dbContext.MenuCategories.Add(mains2);

        dbContext.MenuItems.AddRange(
            new MenuItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId2,
                CategoryId = mains2.Id,
                Name = "Hamburger",
                Description = "Freshly made hamburger with lettuce, tomato, and cheese",
                Price = 5.99m,
                SortOrder = 1,
                IsAvailable = true
            },
            new MenuItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId2,
                CategoryId = mains2.Id,
                Name = "Cheeseburger",
                Description = "Freshly made cheeseburger with lettuce, tomato, and cheese",
                Price = 6.99m,
                SortOrder = 2,
                IsAvailable = true
            }
        );

        for (var i = 0; i < 7; i++)
        {
            dbContext.BusinessHours.Add(new BusinessHour
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId2,
                DayOfWeek = (DayOfWeek)i,
                OpenTime = new TimeOnly(11, 0),
                CloseTime = new TimeOnly(22, 0),
                IsClosed = i == 1
            });
        }

        dbContext.TenantUsers.Add(new TenantUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId2,
            ExternalUserId = "owner-wendys",
            Email = "owner@wendys.com",
            Role = "Owner"
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
