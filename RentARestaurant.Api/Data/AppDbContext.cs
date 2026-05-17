using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Domain;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Tenancy;

namespace RentARestaurant.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    private readonly ITenantContext _tenantContext = tenantContext;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();
    public DbSet<RestaurantProfile> RestaurantProfiles => Set<RestaurantProfile>();
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<BusinessHour> BusinessHours => Set<BusinessHour>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => x.CustomDomain).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Slug).HasMaxLength(120);
            entity.Property(x => x.CustomDomain).HasMaxLength(255);
            entity.Property(x => x.SubscriptionPlan).HasMaxLength(50);
            entity.Property(x => x.SubscriptionState).HasMaxLength(50);
        });

        modelBuilder.Entity<TenantUser>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ExternalUserId }).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.Role).HasMaxLength(50);
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.Users)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RestaurantProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId).IsUnique();
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.Tagline).HasMaxLength(300);
            entity.Property(x => x.PrimaryHexColor).HasMaxLength(12);
            entity.Property(x => x.SecondaryHexColor).HasMaxLength(12);
            entity.HasOne(x => x.Tenant)
                .WithOne(x => x.Profile)
                .HasForeignKey<RestaurantProfile>(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MenuCategory>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.MenuCategories)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MenuItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(140);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.MenuItems)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Category)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BusinessHour>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.DayOfWeek }).IsUnique();
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.BusinessHours)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        ApplyTenantQueryFilters(modelBuilder);
    }

    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (!typeof(ITenantEntity).IsAssignableFrom(clrType))
            {
                continue;
            }

            var method = GetType().GetMethod(nameof(SetTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var genericMethod = method!.MakeGenericMethod(clrType);
            genericMethod.Invoke(this, [modelBuilder]);
        }
    }

    private void SetTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantEntity
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity =>
            _tenantContext.IsSystemRequest ||
            (_tenantContext.TenantId.HasValue && entity.TenantId == _tenantContext.TenantId.Value));
    }
}
