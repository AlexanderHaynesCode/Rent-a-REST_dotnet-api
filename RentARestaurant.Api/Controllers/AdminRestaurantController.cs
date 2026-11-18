using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Contracts;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Domain;
using RentARestaurant.Api.Domain.Entities;
using RentARestaurant.Api.Infrastructure.Storage;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

namespace RentARestaurant.Api.Controllers;

[ApiController]
[Route("api/admin/restaurant")]
public class AdminRestaurantController(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    ITenantAccessService tenantAccessService,
    IR2StorageService r2StorageService,
    IRestaurantAdminService restaurantAdminService) : ControllerBase
{
    private const string Suspended = "suspended";

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [HttpGet]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<PublicRestaurantResponse>> GetCurrent(CancellationToken cancellationToken)
    {
        var data = await restaurantAdminService.GetSnapshotAsync(tenantContext.TenantId!.Value, cancellationToken);
        return Ok(data);
    }

    [HttpGet("bootstrap")]
    [RequireAdminUser]
    public async Task<ActionResult<AdminBootstrapResponse>> GetBootstrap(CancellationToken cancellationToken)
    {
        try
        {
            var externalUserId = HttpContext.Request.Headers["X-Admin-User-Id"].ToString().Trim();
            var resolution = await tenantAccessService.ResolveSingleTenantForAdminAsync(externalUserId, cancellationToken);

            if (resolution.Status == TenantResolutionStatus.NotFound || resolution.Tenant is null)
            {
                return NotFound(new { Error = "No active tenant membership found for this admin user." });
            }

            if (resolution.Status == TenantResolutionStatus.MultipleMatches)
            {
                return Conflict(new
                {
                    Error = "Multiple tenant memberships found for this admin user.",
                    Matches = resolution.MatchCount
                });
            }

            if (!resolution.Tenant.IsActive)
            {
                var suspended = resolution.Tenant.SubscriptionState == Suspended;
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    Error = suspended
                        ? "This tenant is suspended because of a failed payment."
                        : "This tenant's subscription is not active.",
                    Code = suspended ? "payment_failed" : "subscription_inactive"
                });
            }

            tenantContext.SetTenant(resolution.Tenant.TenantId, resolution.Tenant.Slug);

            var restaurant = await restaurantAdminService.GetSnapshotAsync(resolution.Tenant.TenantId, cancellationToken);
            var response = new AdminBootstrapResponse(
                new AdminTenantSummaryResponse(
                    resolution.Tenant.TenantId,
                    resolution.Tenant.Slug,
                    resolution.Tenant.Name,
                    resolution.Tenant.CustomDomain,
                    resolution.Tenant.IsActive,
                    resolution.Tenant.SubscriptionPlan,
                    resolution.Tenant.SubscriptionState),
                restaurant);

            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "DERP An unexpected error occurred during bootstrap.", Details = ex.Message });
        }        
    }

    [HttpPut("subscription")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<AdminTenantSummaryResponse>> UpdateSubscription(
        [FromBody] UpdateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .SingleOrDefaultAsync(x => x.Id == tenantContext.TenantId!.Value, cancellationToken);

        if (tenant is null)
        {
            return NotFound(new { Error = "Tenant not found." });
        }

        tenant.SubscriptionPlan = request.SubscriptionPlan.Trim();

        if (!string.IsNullOrWhiteSpace(request.SubscriptionState))
        {
            tenant.SubscriptionState = request.SubscriptionState.Trim();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new AdminTenantSummaryResponse(
            tenant.Id,
            tenant.Slug,
            tenant.Name,
            tenant.CustomDomain,
            tenant.IsActive,
            tenant.SubscriptionPlan,
            tenant.SubscriptionState));
    }

    [HttpPut("branding")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpdateBranding([FromBody] UpdateBrandingRequest request, CancellationToken cancellationToken)
    {
        var trimmedRequest = request with
        {
            DisplayName = request.DisplayName.Trim(),
            Tagline = request.Tagline.Trim(),
            Announcement = request.Announcement.Trim(),
            PrimaryHexColor = request.PrimaryHexColor.Trim(),
            SecondaryHexColor = request.SecondaryHexColor.Trim(),
            LogoUrl = request.LogoUrl?.Trim(),
            HeroImageUrl = request.HeroImageUrl?.Trim(),
            PrimaryCtaUrl = request.PrimaryCtaUrl?.Trim(),
            Address = NullIfBlank(request.Address),
            PhoneNumber = NullIfBlank(request.PhoneNumber),
            CustomBackgroundHexColor1 = NullIfBlank(request.CustomBackgroundHexColor1),
            CustomBackgroundHexColor2 = NullIfBlank(request.CustomBackgroundHexColor2),
            CustomBackgroundMainHexColor = NullIfBlank(request.CustomBackgroundMainHexColor)
        };

        // The same rules apply to branding changes made by the agent pipeline.
        if (BrandingRules.Validate(trimmedRequest) is { } brandingError)
        {
            return BadRequest(brandingError);
        }

        var updated = await restaurantAdminService.UpdateBrandingAsync(tenantContext.TenantId!.Value, trimmedRequest, cancellationToken);
        if (!updated)
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpPut("hours")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpsertHours([FromBody] IReadOnlyList<UpsertBusinessHourRequest> request, CancellationToken cancellationToken)
    {
        foreach (var item in request)
        {
            if (!BusinessHourRules.AreTimesValid(item.OpenTime, item.CloseTime, item.IsClosed))
                return BadRequest(new { Error = BusinessHourRules.InvalidTimesMessage(item.DayOfWeek.ToString()) });
        }

        await restaurantAdminService.UpsertHoursAsync(tenantContext.TenantId!.Value, request, cancellationToken);
        return NoContent();
    }

    [HttpPut("hours/top-section")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> SetTopHoursSection([FromBody] SetTopHoursSectionRequest request, CancellationToken cancellationToken)
    {
        if (request.TopHoursStyle is null || !TopHoursOptions.Styles.Contains(request.TopHoursStyle))
        {
            return BadRequest(new { message = $"TopHoursStyle must be one of: {string.Join(", ", TopHoursOptions.Styles)}." });
        }

        if (request.TopHoursOpenStatusDetail is null || !TopHoursOptions.OpenStatusDetails.Contains(request.TopHoursOpenStatusDetail))
        {
            return BadRequest(new { message = $"TopHoursOpenStatusDetail must be one of: {string.Join(", ", TopHoursOptions.OpenStatusDetails)}." });
        }

        var updated = await restaurantAdminService.SetTopHoursSectionAsync(tenantContext.TenantId!.Value, request, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpPut("hours/bottom-section")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> SetBottomHoursSection([FromBody] SetBottomHoursSectionRequest request, CancellationToken cancellationToken)
    {
        var updated = await restaurantAdminService.SetBottomHoursSectionAsync(
            tenantContext.TenantId!.Value,
            request.ShowBottomHoursSection,
            request.UseLegacyBottomHoursStyle,
            cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpPost("hours/date-specific")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpsertDateSpecificHours([FromBody] IReadOnlyList<UpsertDateSpecificHourRequest> request, CancellationToken cancellationToken)
    {
        try 
        {
            if (request.Count == 0 || request.Count > 7)
                return BadRequest(new { Error = "You must provide between 1 and 7 date-specific hours." });

            foreach (var item in request)
            {
                if (!BusinessHourRules.AreTimesValid(item.OpenTime, item.CloseTime, item.IsClosed))
                    return BadRequest(new { Error = BusinessHourRules.InvalidTimesMessage($"{item.Date:yyyy-MM-dd}") });
            }

            var dates = request.Select(x => x.Date).ToList();
            if (dates.Distinct().Count() != dates.Count)
                return BadRequest(new { Error = "Duplicate dates are not allowed." });

            await restaurantAdminService.UpsertDateSpecificHoursAsync(tenantContext.TenantId!.Value, request, cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to upsert date-specific hours.", Details = ex.Message });
        }
    }

    [HttpPost("images/{imageType}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UploadBrandingImage(
        string imageType,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var normalizedImageType = imageType.Trim();
        if (!IsSupportedBrandingImageType(normalizedImageType))
        {
            return BadRequest(new { Error = "imageType must be one of: logo, hero, primary-cta, custom-background." });
        }

        if (ValidateImageFile(file) is { } fileError)
        {
            return BadRequest(new { Error = fileError });
        }

        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantContext.TenantId!.Value, cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        string url;
        try
        {
            await using (var stream = file.OpenReadStream())
            {
                url = await r2StorageService.UploadImageAsync(
                    tenantContext.TenantId!.Value,
                    normalizedImageType,
                    stream,
                    file.ContentType,
                    cancellationToken);
            }
        } 
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to upload image.", Details = ex.Message });
        }

        // Each image type is stored under one fixed R2 key, so a replacement would otherwise keep the
        // same URL and browsers/the CDN would keep serving the cached old image. A per-upload version
        // makes the stored URL change every time (the key stays fixed so delete still finds it).
        url = WithUploadVersion(url);

        SetBrandingImageUrl(profile, normalizedImageType, url);

        profile.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { url });
    }

    [HttpDelete("images/{imageType}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> DeleteBrandingImage(
        string imageType,
        CancellationToken cancellationToken)
    {
        var normalizedImageType = imageType.Trim();
        if (!IsSupportedBrandingImageType(normalizedImageType))
        {
            return BadRequest(new { Error = "imageType must be one of: logo, hero, primary-cta, custom-background." });
        }

        var profile = await dbContext.RestaurantProfiles
            .SingleOrDefaultAsync(x => x.TenantId == tenantContext.TenantId!.Value, cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        try
        {
            await r2StorageService.DeleteImageAsync(
                tenantContext.TenantId!.Value,
                normalizedImageType,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to delete image.", Details = ex.Message });
        }

        SetBrandingImageUrl(profile, normalizedImageType, null);
        profile.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpPost("menu/categories")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<Guid>> CreateCategory([FromBody] CreateMenuCategoryRequest request, CancellationToken cancellationToken)
    {
        var description = request.Description?.Trim();
        if (description?.Length > MenuLimits.MaxDescriptionLength)
        {
            return BadRequest(new { Error = $"Category description must be {MenuLimits.MaxDescriptionLength} characters or fewer." });
        }

        var trimmedRequest = request with { Name = request.Name.Trim(), Description = description };
        var result = await restaurantAdminService.CreateMenuCategoryAsync(tenantContext.TenantId!.Value, trimmedRequest, cancellationToken);

        return result.Status switch
        {
            MenuMutationStatus.Success => CreatedAtAction(nameof(GetCurrent), new { }, result.Id),
            MenuMutationStatus.LimitReached => Conflict(new { Error = $"You can have up to {MenuLimits.MaxCategoriesPerTenant} menu categories." }),
            MenuMutationStatus.DuplicateName => Conflict(new { Error = "That menu already has a category with that name." }),
            MenuMutationStatus.MenuInvalid => BadRequest(new { Error = "Menu does not exist for this tenant." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpPut("menu/categories/{id:guid}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpdateCategory(
        Guid id,
        [FromBody] UpdateMenuCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        var description = request.Description.Trim();

        if (description.Length > MenuLimits.MaxDescriptionLength)
        {
            return BadRequest(new { Error = $"Category description must be {MenuLimits.MaxDescriptionLength} characters or fewer." });
        }

        var result = await restaurantAdminService.UpdateMenuCategoryAsync(
            tenantContext.TenantId!.Value,
            id,
            name,
            description,
            request.SortOrder,
            request.MenuId,
            cancellationToken);

        return result.Status switch
        {
            MenuMutationStatus.Success => NoContent(),
            MenuMutationStatus.NotFound => NotFound(),
            MenuMutationStatus.DuplicateName => Conflict(new { Error = "That menu already has a category with that name." }),
            MenuMutationStatus.MenuInvalid => BadRequest(new { Error = "Menu does not exist for this tenant." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpPost("menus")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<Guid>> CreateMenu([FromBody] CreateMenuRequest request, CancellationToken cancellationToken)
    {
        var trimmedRequest = request with { Name = request.Name.Trim(), Description = request.Description?.Trim() };
        if (ValidateMenuFields(trimmedRequest.Name, trimmedRequest.Description) is { } error)
        {
            return BadRequest(new { Error = error });
        }

        var result = await restaurantAdminService.CreateMenuAsync(tenantContext.TenantId!.Value, trimmedRequest, cancellationToken);

        return result.Status switch
        {
            MenuMutationStatus.Success => CreatedAtAction(nameof(GetCurrent), new { }, result.Id),
            MenuMutationStatus.LimitReached => Conflict(new { Error = $"You can have up to {MenuLimits.MaxMenusPerTenant} menus." }),
            MenuMutationStatus.DuplicateName => Conflict(new { Error = "A menu with that name already exists." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpPut("menus/{id:guid}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpdateMenu(Guid id, [FromBody] UpdateMenuRequest request, CancellationToken cancellationToken)
    {
        var trimmedRequest = request with
        {
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            BackgroundStyle = NullIfBlank(request.BackgroundStyle),
            BackgroundHexColor = NullIfBlank(request.BackgroundHexColor),
            BackgroundPattern = NullIfBlank(request.BackgroundPattern)
        };
        var error = ValidateMenuFields(trimmedRequest.Name, trimmedRequest.Description)
            ?? ValidateMenuBackground(trimmedRequest);
        if (error is not null)
        {
            return BadRequest(new { Error = error });
        }

        var result = await restaurantAdminService.UpdateMenuAsync(tenantContext.TenantId!.Value, id, trimmedRequest, cancellationToken);

        return result.Status switch
        {
            MenuMutationStatus.Success => NoContent(),
            MenuMutationStatus.NotFound => NotFound(),
            MenuMutationStatus.DuplicateName => Conflict(new { Error = "A menu with that name already exists." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpDelete("menus/{id:guid}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> DeleteMenu(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;
        var backgroundImageUrl = await dbContext.Menus
            .AsNoTracking()
            .Where(x => x.Id == id && x.TenantId == tenantId)
            .Select(x => x.BackgroundImageUrl)
            .SingleOrDefaultAsync(cancellationToken);

        var result = await restaurantAdminService.DeleteMenuAsync(tenantId, id, cancellationToken);
        if (result.Status == MenuMutationStatus.Success)
        {
            await DeleteMenuBackgroundImageIfUnusedAsync(tenantId, backgroundImageUrl, cancellationToken);
        }

        return result.Status switch
        {
            MenuMutationStatus.Success => NoContent(),
            MenuMutationStatus.NotFound => NotFound(),
            MenuMutationStatus.LastMenu => Conflict(new { Error = "Your site needs at least one menu, so the last menu can't be deleted." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Copies the menu with all of its categories and items into a new menu with the given name.</summary>
    [HttpPost("menus/{id:guid}/clone")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<ClonedMenuResponse>> CloneMenu(Guid id, [FromBody] CloneMenuRequest request, CancellationToken cancellationToken)
    {
        var trimmedRequest = request with { Name = request.Name?.Trim() ?? string.Empty, Description = request.Description?.Trim() };
        if (ValidateMenuFields(trimmedRequest.Name!, trimmedRequest.Description) is { } error)
        {
            return BadRequest(new { Error = error });
        }

        var result = await restaurantAdminService.CloneMenuAsync(tenantContext.TenantId!.Value, id, trimmedRequest, cancellationToken);

        return result.Status switch
        {
            MenuCloneStatus.Success => CreatedAtAction(nameof(GetCurrent), new { }, result.Copy),
            MenuCloneStatus.NotFound => NotFound(new { Error = "That menu could not be found. Refresh the page and try again." }),
            MenuCloneStatus.DuplicateName => Conflict(new { Error = "A menu with that name already exists." }),
            MenuCloneStatus.LimitReached => Conflict(new { Error = MenuCloneRules.FailureMessage(result.SourceName!, result.LimitProblems!) }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// Uploads the menu's background image and switches the menu's background to it. The previous image is
    /// removed from R2 once no menu (e.g. a clone) uses it any more.
    /// </summary>
    [HttpPost("menus/{id:guid}/background-image")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UploadMenuBackgroundImage(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        if (ValidateImageFile(file) is { } fileError)
        {
            return BadRequest(new { Error = fileError });
        }

        var tenantId = tenantContext.TenantId!.Value;
        var menu = await dbContext.Menus.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);
        if (menu is null)
        {
            return NotFound(new { Error = "That menu could not be found. Refresh the page and try again." });
        }

        string url;
        try
        {
            await using var stream = file.OpenReadStream();
            url = await r2StorageService.UploadImageAsync(
                tenantId,
                MenuBackgroundRules.NewImageType(menu.Id),
                stream,
                file.ContentType,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Failed to upload image.", Details = ex.Message });
        }

        var previousUrl = menu.BackgroundImageUrl;
        menu.BackgroundImageUrl = url;
        menu.BackgroundStyle = MenuBackgroundRules.ImageStyle;
        await dbContext.SaveChangesAsync(cancellationToken);

        await DeleteMenuBackgroundImageIfUnusedAsync(tenantId, previousUrl, cancellationToken);
        return Ok(new { url });
    }

    /// <summary>Clears the menu's background image (its background style is left as is).</summary>
    [HttpDelete("menus/{id:guid}/background-image")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> DeleteMenuBackgroundImage(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;
        var menu = await dbContext.Menus.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);
        if (menu is null)
        {
            return NotFound(new { Error = "That menu could not be found. Refresh the page and try again." });
        }

        var previousUrl = menu.BackgroundImageUrl;
        if (previousUrl is not null)
        {
            menu.BackgroundImageUrl = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            await DeleteMenuBackgroundImageIfUnusedAsync(tenantId, previousUrl, cancellationToken);
        }

        return NoContent();
    }

    [HttpPut("menus/reorder")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> ReorderMenus([FromBody] IReadOnlyList<MenuReorderEntry> request, CancellationToken cancellationToken)
    {
        var ids = request.Select(x => x.Id).ToList();
        var menus = await dbContext.Menus
            .Where(x => x.TenantId == tenantContext.TenantId!.Value && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        foreach (var entry in request)
        {
            var menu = menus.FirstOrDefault(x => x.Id == entry.Id);
            if (menu is not null)
                menu.SortOrder = entry.SortOrder;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("menus/settings")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> SetMenuSettings([FromBody] SetMenuSettingsRequest request, CancellationToken cancellationToken)
    {
        var updated = await restaurantAdminService.SetMenuSettingsAsync(
            tenantContext.TenantId!.Value,
            request.AllowMenuPdfDownload,
            cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpPost("menu/items")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<Guid>> CreateMenuItem([FromBody] CreateMenuItemRequest request, CancellationToken cancellationToken)
    {
        var trimmedRequest = request with { Name = request.Name.Trim(), Description = request.Description.Trim() };
        var result = await restaurantAdminService.CreateMenuItemAsync(tenantContext.TenantId!.Value, trimmedRequest, cancellationToken);

        if (result.Status == MenuItemMutationStatus.CategoryInvalid)
        {
            return BadRequest(new { Error = "Category does not exist for this tenant." });
        }

        if (result.Status == MenuItemMutationStatus.LimitReached)
        {
            return Conflict(new { Error = $"You can have up to {MenuLimits.MaxItemsPerTenant} menu items." });
        }

        return CreatedAtAction(nameof(GetCurrent), new { }, result.ItemId);
    }

    [HttpGet("hours/date-specific")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<ActionResult<IReadOnlyList<AdminDateSpecificHourResponse>>> GetDateSpecificHours(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var hours = await dbContext.BusinessHours
            .Where(x => x.TenantId == tenantContext.TenantId!.Value && x.Date != null && x.Date >= today)
            .AsNoTracking()
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        var response = hours.Select(h => new AdminDateSpecificHourResponse(
            h.Date!.Value,
            h.DayOfWeek,
            h.OpenTime.ToString("HH:mm"),
            h.CloseTime.ToString("HH:mm"),
            h.IsClosed)).ToList();

        return Ok(response);
    }

    [HttpDelete("hours/date-specific/{date}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> DeleteDateSpecificHour(DateOnly date, CancellationToken cancellationToken)
    {
        var deleted = await restaurantAdminService.DeleteDateSpecificHourAsync(tenantContext.TenantId!.Value, date, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpDelete("menu/categories/{id:guid}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await restaurantAdminService.DeleteMenuCategoryAsync(tenantContext.TenantId!.Value, id, cancellationToken);
        if (!deleted)
            return NotFound();

        return NoContent();
    }

    [HttpDelete("menu/items/{id:guid}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> DeleteMenuItem(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await restaurantAdminService.DeleteMenuItemAsync(tenantContext.TenantId!.Value, id, cancellationToken);
        if (!deleted)
            return NotFound();

        return NoContent();
    }

    [HttpPut("menu/items/{id:guid}")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> UpdateMenuItem(Guid id, [FromBody] UpdateMenuItemRequest request, CancellationToken cancellationToken)
    {
        var trimmedRequest = request with { Name = request.Name.Trim(), Description = request.Description.Trim() };
        var result = await restaurantAdminService.UpdateMenuItemAsync(tenantContext.TenantId!.Value, id, trimmedRequest, cancellationToken);

        if (result.Status == MenuItemMutationStatus.NotFound)
            return NotFound();

        if (result.Status == MenuItemMutationStatus.CategoryInvalid)
            return BadRequest(new { Error = "Category does not exist for this tenant." });

        return NoContent();
    }

    [HttpPut("menu/categories/reorder")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> ReorderCategories([FromBody] IReadOnlyList<MenuReorderEntry> request, CancellationToken cancellationToken)
    {
        var ids = request.Select(x => x.Id).ToList();
        var categories = await dbContext.MenuCategories
            .Where(x => x.TenantId == tenantContext.TenantId!.Value && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        foreach (var entry in request)
        {
            var category = categories.FirstOrDefault(x => x.Id == entry.Id);
            if (category is not null)
                category.SortOrder = entry.SortOrder;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("menu/items/reorder")]
    [RequireTenantContext]
    [RequireAdminAccess]
    public async Task<IActionResult> ReorderMenuItems([FromBody] IReadOnlyList<MenuReorderEntry> request, CancellationToken cancellationToken)
    {
        var ids = request.Select(x => x.Id).ToList();
        var items = await dbContext.MenuItems
            .Where(x => x.TenantId == tenantContext.TenantId!.Value && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        foreach (var entry in request)
        {
            var item = items.FirstOrDefault(x => x.Id == entry.Id);
            if (item is not null)
                item.SortOrder = entry.SortOrder;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string? ValidateMenuFields(string name, string? description)
    {
        if (name.Length == 0)
            return "Menu name is required.";
        if (name.Length > MenuLimits.MaxNameLength)
            return $"Menu name must be {MenuLimits.MaxNameLength} characters or fewer.";
        if (description?.Length > MenuLimits.MaxDescriptionLength)
            return $"Menu description must be {MenuLimits.MaxDescriptionLength} characters or fewer.";
        return null;
    }

    /// <summary>
    /// A color or pattern background has to come with its value; the image style may have no image yet (the
    /// tenant uploads one next), which draws no background.
    /// </summary>
    private static string? ValidateMenuBackground(UpdateMenuRequest request)
    {
        if (MenuBackgroundRules.Validate(request.BackgroundStyle, request.BackgroundHexColor, request.BackgroundPattern) is { } error)
            return error;
        if (request.BackgroundStyle == MenuBackgroundRules.ColorStyle && request.BackgroundHexColor is null)
            return "Pick a background color for the menu.";
        if (request.BackgroundStyle == MenuBackgroundRules.PatternStyle && request.BackgroundPattern is null)
            return "Pick a background pattern for the menu.";
        return null;
    }

    private static string? ValidateImageFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return "No file provided.";

        var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png" };
        if (!allowedContentTypes.Contains(file.ContentType))
            return "Only JPEG and PNG images are accepted.";

        const long maxBytes = 10 * 1024 * 1024; // 10 MB
        if (file.Length > maxBytes)
            return "File size must not exceed 10 MB.";

        return null;
    }

    /// <summary>
    /// Removes a replaced or deleted menu background image from R2 unless another menu (a clone) still shows it.
    /// Best effort: the menu change is already saved, and a leftover file only costs storage.
    /// </summary>
    private async Task DeleteMenuBackgroundImageIfUnusedAsync(Guid tenantId, string? url, CancellationToken cancellationToken)
    {
        if (MenuBackgroundRules.ImageTypeFromUrl(tenantId, url) is not { } imageType)
        {
            return;
        }

        var stillUsed = await dbContext.Menus.AnyAsync(x => x.TenantId == tenantId && x.BackgroundImageUrl == url, cancellationToken);
        if (stillUsed)
        {
            return;
        }

        try
        {
            await r2StorageService.DeleteImageAsync(tenantId, imageType, cancellationToken);
        }
        catch (Exception)
        {
            // Ignored on purpose; see the summary.
        }
    }

    private static bool IsSupportedBrandingImageType(string imageType) =>
        imageType.Equals("logo", StringComparison.OrdinalIgnoreCase) ||
        imageType.Equals("hero", StringComparison.OrdinalIgnoreCase) ||
        imageType.Equals("primary-cta", StringComparison.OrdinalIgnoreCase) ||
        imageType.Equals("custom-background", StringComparison.OrdinalIgnoreCase);

    private static string WithUploadVersion(string url)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private static void SetBrandingImageUrl(RestaurantProfile profile, string imageType, string? url)
    {
        switch (imageType.ToLowerInvariant())
        {
            case "logo":
                profile.LogoUrl = url;
                break;
            case "hero":
                profile.HeroImageUrl = url;
                break;
            case "primary-cta":
                profile.PrimaryCtaUrl = url;
                break;
            case "custom-background":
                profile.CustomBackgroundImageUrl = url;
                break;
        }
    }
}
