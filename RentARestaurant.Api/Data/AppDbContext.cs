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
            entity.ToTable("tenants", "public");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => x.CustomDomain).IsUnique();
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(120);
            entity.Property(x => x.CustomDomain).HasColumnName("custom_domain").HasMaxLength(255);
            entity.Property(x => x.IsActive).HasColumnName("is_active");
            entity.Property(x => x.SubscriptionPlan).HasColumnName("subscription_plan").HasMaxLength(50);
            entity.Property(x => x.SubscriptionState).HasColumnName("subscription_state").HasMaxLength(50);
            entity.Property(x => x.CreatedUtc).HasColumnName("created_utc");
        });

        modelBuilder.Entity<TenantUser>(entity =>
        {
            entity.ToTable("tenant_users", "public");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ExternalUserId).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ExternalUserId }).IsUnique();
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ExternalUserId).HasColumnName("external_user_id");
            entity.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
            entity.Property(x => x.Role).HasColumnName("role").HasMaxLength(50);
            entity.Property(x => x.CreatedUtc).HasColumnName("created_utc");
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.Users)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RestaurantProfile>(entity =>
        {
            entity.ToTable("restaurant_profiles", "public");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId).IsUnique();
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            entity.Property(x => x.Tagline).HasColumnName("tagline").HasMaxLength(300);
            entity.Property(x => x.PrimaryHexColor).HasColumnName("primary_hex_color").HasMaxLength(12);
            entity.Property(x => x.SecondaryHexColor).HasColumnName("secondary_hex_color").HasMaxLength(12);
            entity.Property(x => x.LogoUrl).HasColumnName("logo_url");
            entity.Property(x => x.HeroImageUrl).HasColumnName("hero_image_url");
            entity.Property(x => x.PrimaryCtaUrl).HasColumnName("primary_cta_url");
            entity.Property(x => x.UpdatedUtc).HasColumnName("updated_utc");
            entity.HasOne(x => x.Tenant)
                .WithOne(x => x.Profile)
                .HasForeignKey<RestaurantProfile>(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MenuCategory>(entity =>
        {
            entity.ToTable("menu_categories", "public");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.MenuCategories)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MenuItem>(entity =>
        {
            entity.ToTable("menu_items", "public");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.CategoryId).HasColumnName("category_id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(140);
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(x => x.Price).HasColumnName("price");
            entity.Property(x => x.IsAvailable).HasColumnName("is_available");
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
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
            entity.ToTable("business_hours", "public");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.DayOfWeek }).IsUnique();
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.DayOfWeek).HasColumnName("day_of_week");
            entity.Property(x => x.OpenTime).HasColumnName("open_time");
            entity.Property(x => x.CloseTime).HasColumnName("close_time");
            entity.Property(x => x.IsClosed).HasColumnName("is_closed");
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
