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
    public DbSet<AgentSubmission> AgentSubmissions => Set<AgentSubmission>();
    public DbSet<AgentChangeAudit> AgentChangeAudits => Set<AgentChangeAudit>();

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
            entity.HasIndex(x => new { x.TenantId, x.DayOfWeek, x.Date }).IsUnique();
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.DayOfWeek).HasColumnName("day_of_week");
            entity.Property(x => x.OpenTime).HasColumnName("open_time");
            entity.Property(x => x.CloseTime).HasColumnName("close_time");
            entity.Property(x => x.IsClosed).HasColumnName("is_closed");
            entity.Property(x => x.Date).HasColumnName("date");
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.BusinessHours)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSubmission>(entity =>
        {
            entity.ToTable("agent_submissions", "public");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(20);
            entity.Property(x => x.SenderIdentifier).HasColumnName("sender_identifier").HasMaxLength(320);
            entity.Property(x => x.RawBodyText).HasColumnName("raw_body_text");
            entity.Property(x => x.AttachmentRefs).HasColumnName("attachment_refs");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.TranslatedJson).HasColumnName("translated_json");
            entity.Property(x => x.ValidatorConfidence).HasColumnName("validator_confidence");
            entity.Property(x => x.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(1000);
            entity.Property(x => x.ReceivedUtc).HasColumnName("received_utc");
            entity.Property(x => x.UpdatedUtc).HasColumnName("updated_utc");
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.AgentSubmissions)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentChangeAudit>(entity =>
        {
            entity.ToTable("agent_change_audits", "public");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.RollbackExpiresUtc });
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.SubmissionId).HasColumnName("submission_id");
            entity.Property(x => x.PreChangeSnapshotJson).HasColumnName("pre_change_snapshot_json");
            entity.Property(x => x.DiffSummaryJson).HasColumnName("diff_summary_json");
            entity.Property(x => x.AppliedUtc).HasColumnName("applied_utc");
            entity.Property(x => x.RollbackExpiresUtc).HasColumnName("rollback_expires_utc");
            entity.Property(x => x.RolledBack).HasColumnName("rolled_back");
            entity.Property(x => x.RolledBackUtc).HasColumnName("rolled_back_utc");
            entity.HasOne(x => x.Tenant)
                .WithMany(x => x.AgentChangeAudits)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Submission)
                .WithMany()
                .HasForeignKey(x => x.SubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
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
