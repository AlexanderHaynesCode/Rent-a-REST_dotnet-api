using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Tests;

/// <summary>
/// Covers the snapshot taken before an agent change and the rollback that restores it. Runs on the EF Core
/// in-memory provider, which does not enforce unique indexes or CHECK constraints.
/// </summary>
public class SnapshotRestoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _lunchMenuId = Guid.NewGuid();
    private readonly Guid _dinnerMenuId = Guid.NewGuid();
    private readonly Guid _saladsId = Guid.NewGuid();
    private readonly Guid _steaksId = Guid.NewGuid();
    private readonly Guid _caesarId = Guid.NewGuid();
    private readonly Guid _ribeyeId = Guid.NewGuid();
    private readonly AppDbContext _db;
    private readonly RestaurantAdminService _service;

    public SnapshotRestoreTests()
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(_tenantId, "test-tenant");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(options, tenantContext);
        _service = new RestaurantAdminService(_db);

        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Test Tenant", Slug = "test-tenant", SubscriptionPlan = "Done-For-You" });
        _db.RestaurantProfiles.Add(new RestaurantProfile
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DisplayName = "Test Tenant",
            Tagline = "Original tagline",
            HeroImageUrl = "https://cdn.example.com/hero.png",
            ShowCustomBackground = true,
            CustomBackgroundHexColor1 = "#112233",
            CustomBackgroundHexColor2 = "#445566",
            CustomBackgroundMainHexColor = "#FAFAFA",
            CustomBackgroundImageUrl = "https://cdn.example.com/page.png",
            ContactHoursWhiteBackground = true,
            ContactHoursWhiteBackgroundTransparency = 30,
            TopHoursStyle = TopHoursOptions.OpenStatusStyle,
            TopHoursOpenStatusDetail = TopHoursOptions.TodayDetail
        });
        _db.Menus.AddRange(
            new Menu { Id = _lunchMenuId, TenantId = _tenantId, Name = "Lunch", SortOrder = 0 },
            new Menu { Id = _dinnerMenuId, TenantId = _tenantId, Name = "Dinner", Description = "After 5", SortOrder = 1, IsVisible = false });
        _db.MenuCategories.AddRange(
            new MenuCategory { Id = _saladsId, TenantId = _tenantId, MenuId = _lunchMenuId, Name = "Salads", SortOrder = 0 },
            new MenuCategory { Id = _steaksId, TenantId = _tenantId, MenuId = _dinnerMenuId, Name = "Steaks", SortOrder = 0 });
        _db.MenuItems.AddRange(
            new MenuItem { Id = _caesarId, TenantId = _tenantId, CategoryId = _saladsId, Name = "Caesar", Price = 9m },
            new MenuItem { Id = _ribeyeId, TenantId = _tenantId, CategoryId = _steaksId, Name = "Ribeye", Price = 32m });
        _db.BusinessHours.Add(new BusinessHour
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DayOfWeek = DayOfWeek.Friday,
            OpenTime = new TimeOnly(10, 0),
            CloseTime = new TimeOnly(2, 0)
        });
        _db.SaveChanges();
    }

    private Task<PublicRestaurantResponse> SnapshotAsync() => _service.GetSnapshotAsync(_tenantId, CancellationToken.None);

    private static string Json(PublicRestaurantResponse snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    private async Task AssertRestoresAsync(PublicRestaurantResponse snapshot)
    {
        await _service.RestoreSnapshotAsync(_tenantId, snapshot, presentKeys: null, CancellationToken.None);
        _db.ChangeTracker.Clear();
        Assert.Equal(Json(snapshot), Json(await SnapshotAsync()));
    }

    [Fact]
    public async Task Restores_every_profile_setting()
    {
        var snapshot = await SnapshotAsync();

        var profile = await _db.RestaurantProfiles.SingleAsync();
        profile.Tagline = "Changed";
        profile.HideHeroImage = true;
        profile.ShowTopContactSection = true;
        profile.ShowBottomContactSection = false;
        profile.ContactHoursWhiteBackground = false;
        profile.ContactHoursWhiteBackgroundTransparency = 80;
        profile.ApplyTransparencyToMenuItems = true;
        profile.ShowCustomBackground = false;
        profile.CustomBackgroundHexColor1 = "#000000";
        profile.CustomBackgroundHexColor2 = null;
        profile.CustomBackgroundPosition1X = 50;
        profile.CustomBackgroundPosition1Y = 60;
        profile.CustomBackgroundPosition2X = 70;
        profile.CustomBackgroundPosition2Y = 90;
        profile.CustomBackgroundMainHexColor = null;
        profile.CustomBackgroundMainColorActive = false;
        profile.CustomBackgroundSplash1Active = false;
        profile.CustomBackgroundSplash2Active = false;
        profile.CustomBackgroundImageUrl = null;
        profile.CustomBackgroundImageActive = false;
        profile.ShowTopHoursSection = false;
        profile.TopHoursStyle = TopHoursOptions.StandardStyle;
        profile.TopHoursOpenStatusDetail = TopHoursOptions.NoDetail;
        profile.UseLegacyHoursStyle = true;
        profile.ShowBottomHoursSection = false;
        profile.UseLegacyBottomHoursStyle = true;
        profile.AllowMenuPdfDownload = false;
        await _db.SaveChangesAsync();

        await AssertRestoresAsync(snapshot);
    }

    [Fact]
    public async Task Restores_overnight_hours()
    {
        var snapshot = await SnapshotAsync();

        var friday = await _db.BusinessHours.SingleAsync();
        friday.CloseTime = new TimeOnly(22, 0);
        await _db.SaveChangesAsync();

        await AssertRestoresAsync(snapshot);
        Assert.Equal("02:00", (await SnapshotAsync()).Hours.Single().CloseTime);
    }

    [Fact]
    public async Task Brings_back_a_deleted_menu_with_its_categories_and_items()
    {
        var snapshot = await SnapshotAsync();

        await _service.DeleteMenuAsync(_tenantId, _dinnerMenuId, CancellationToken.None);
        Assert.DoesNotContain(await _db.Menus.ToListAsync(), m => m.Id == _dinnerMenuId);

        await AssertRestoresAsync(snapshot);

        var steaks = await _db.MenuCategories.SingleAsync(c => c.Id == _steaksId);
        Assert.Equal(_dinnerMenuId, steaks.MenuId);
        Assert.False((await _db.Menus.SingleAsync(m => m.Id == _dinnerMenuId)).IsVisible);
    }

    [Fact]
    public async Task Removes_menus_categories_and_items_created_after_the_snapshot()
    {
        var snapshot = await SnapshotAsync();

        var brunch = await _service.CreateMenuAsync(_tenantId, new CreateMenuRequest("Brunch", 2), CancellationToken.None);
        var pancakes = await _service.CreateMenuCategoryAsync(
            _tenantId, new CreateMenuCategoryRequest("Pancakes", 0, null, brunch.Id), CancellationToken.None);
        await _service.CreateMenuItemAsync(
            _tenantId, new CreateMenuItemRequest(pancakes.Id!.Value, "Buttermilk Stack", "", 9m, true, 0), CancellationToken.None);

        await AssertRestoresAsync(snapshot);
        Assert.Equal(2, await _db.Menus.CountAsync());
    }

    [Fact]
    public async Task Restores_menu_names_visibility_and_order_even_when_names_were_swapped()
    {
        var snapshot = await SnapshotAsync();

        var lunch = await _db.Menus.SingleAsync(m => m.Id == _lunchMenuId);
        var dinner = await _db.Menus.SingleAsync(m => m.Id == _dinnerMenuId);
        lunch.Name = "Dinner";
        lunch.IsVisible = false;
        lunch.SortOrder = 5;
        dinner.Name = "Lunch";
        dinner.IsVisible = true;
        dinner.Description = "";
        await _db.SaveChangesAsync();

        await AssertRestoresAsync(snapshot);
    }

    [Fact]
    public async Task Moves_categories_and_items_back_out_of_things_that_get_removed()
    {
        var snapshot = await SnapshotAsync();

        // The Steaks category moves to a new menu and the Ribeye into a new category; both new homes are
        // removed by the restore, which must not take the moved rows with them.
        var late = await _service.CreateMenuAsync(_tenantId, new CreateMenuRequest("Late Night", 2), CancellationToken.None);
        var snacks = await _service.CreateMenuCategoryAsync(
            _tenantId, new CreateMenuCategoryRequest("Snacks", 0, null, late.Id), CancellationToken.None);
        var steaks = await _db.MenuCategories.SingleAsync(c => c.Id == _steaksId);
        steaks.MenuId = late.Id!.Value;
        var ribeye = await _db.MenuItems.SingleAsync(i => i.Id == _ribeyeId);
        ribeye.CategoryId = snacks.Id!.Value;
        await _db.SaveChangesAsync();

        await AssertRestoresAsync(snapshot);
    }

    [Fact]
    public async Task Leaves_settings_alone_that_an_older_snapshot_does_not_contain()
    {
        // An audit stored before these settings existed: its JSON has no key for them.
        var olderJson = JsonNode.Parse(Json(await SnapshotAsync()))!.AsObject();
        foreach (var key in new[]
        {
            "hideHeroImage", "contactHoursWhiteBackground", "contactHoursWhiteBackgroundTransparency",
            "applyTransparencyToMenuItems", "showTopContactSection", "showBottomContactSection",
            "customBackgroundImageUrl", "customBackgroundImageActive", "allowMenuPdfDownload", "menus"
        })
        {
            olderJson.Remove(key);
        }

        var profile = await _db.RestaurantProfiles.SingleAsync();
        profile.Tagline = "Changed";
        profile.HideHeroImage = true;
        profile.ContactHoursWhiteBackgroundTransparency = 80;
        profile.CustomBackgroundImageUrl = "https://cdn.example.com/newer.png";
        profile.AllowMenuPdfDownload = false;
        var dinner = await _db.Menus.SingleAsync(m => m.Id == _dinnerMenuId);
        dinner.Name = "Supper";
        await _db.SaveChangesAsync();

        var audit = await AddAuditAsync(olderJson.ToJsonString());
        var result = await new AgentRollbackService(_db, _service).RollbackAsync(_tenantId, audit.Id, CancellationToken.None);
        _db.ChangeTracker.Clear();

        Assert.True(result.Succeeded);
        var restored = await SnapshotAsync();
        Assert.Equal("Original tagline", restored.Tagline);
        Assert.True(restored.HideHeroImage);
        Assert.True(restored.ContactHoursWhiteBackground);
        Assert.Equal(80, restored.ContactHoursWhiteBackgroundTransparency);
        Assert.Equal("https://cdn.example.com/newer.png", restored.CustomBackgroundImageUrl);
        Assert.False(restored.AllowMenuPdfDownload);
        Assert.Contains(restored.Menus!, m => m.Name == "Supper");
        Assert.Equal(_dinnerMenuId, restored.Menu.Single(c => c.Id == _steaksId).MenuId);
    }

    [Fact]
    public async Task Rollback_of_a_current_snapshot_restores_everything_and_marks_the_audit()
    {
        var snapshot = await SnapshotAsync();
        var audit = await AddAuditAsync(Json(snapshot));

        var profile = await _db.RestaurantProfiles.SingleAsync();
        profile.HideHeroImage = true;
        profile.CustomBackgroundImageUrl = null;
        await _db.SaveChangesAsync();
        await _service.DeleteMenuAsync(_tenantId, _dinnerMenuId, CancellationToken.None);

        var result = await new AgentRollbackService(_db, _service).RollbackAsync(_tenantId, auditId: null, CancellationToken.None);
        _db.ChangeTracker.Clear();

        Assert.True(result.Succeeded);
        Assert.Equal(Json(snapshot), Json(await SnapshotAsync()));
        Assert.True((await _db.AgentChangeAudits.SingleAsync(a => a.Id == audit.Id)).RolledBack);
    }

    private async Task<AgentChangeAudit> AddAuditAsync(string snapshotJson)
    {
        var submission = new AgentSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Channel = "email",
            SenderIdentifier = "owner@example.com"
        };
        var audit = new AgentChangeAudit
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            SubmissionId = submission.Id,
            PreChangeSnapshotJson = snapshotJson,
            DiffSummaryJson = "[\"Changed something.\"]",
            AppliedUtc = DateTime.UtcNow.AddMinutes(-5),
            RollbackExpiresUtc = DateTime.UtcNow.AddDays(30)
        };
        _db.AgentSubmissions.Add(submission);
        _db.AgentChangeAudits.Add(audit);
        await _db.SaveChangesAsync();
        return audit;
    }
}
