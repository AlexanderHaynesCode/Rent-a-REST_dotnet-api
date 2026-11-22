using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Infrastructure.Agent;
using RentARestaurant.Api.Infrastructure.Email;
using RentARestaurant.Api.Infrastructure.Provisioning;
using RentARestaurant.Api.Infrastructure.Storage;
using RentARestaurant.Api.Infrastructure.Stripe;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;
using Resend;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TenantResolutionOptions>(
    builder.Configuration.GetSection(TenantResolutionOptions.SectionName));
builder.Services.Configure<LocalMediaStorageOptions>(
    builder.Configuration.GetSection(LocalMediaStorageOptions.SectionName));
builder.Services.Configure<StripeOptions>(
    builder.Configuration.GetSection(StripeOptions.SectionName));
builder.Services.Configure<MarketingOptions>(
    builder.Configuration.GetSection(MarketingOptions.SectionName));
builder.Services.Configure<AdminOptions>(
    builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.Configure<ProvisioningOptions>(
    builder.Configuration.GetSection(ProvisioningOptions.SectionName));
builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.Configure<CloudflareR2Options>(
    builder.Configuration.GetSection(CloudflareR2Options.SectionName));
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

// builder.Services.AddDbContext<AppDbContext>(options =>
//     options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQLConnection")));

builder.Services.AddResend(o =>
{
    o.ApiToken = builder.Configuration["Email:ApiKey"] ?? string.Empty;
});

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
builder.Services.AddScoped<IEmailService, ResendEmailService>();
builder.Services.AddScoped<IMediaNamespaceProvisioner, LocalMediaNamespaceProvisioner>();
builder.Services.AddScoped<ITenantAccessService, TenantAccessService>();
builder.Services.AddScoped<IRestaurantAdminService, RestaurantAdminService>();
builder.Services.AddScoped<IAgentRollbackService, AgentRollbackService>();
builder.Services.AddScoped<IStripeCheckoutService, StripeCheckoutService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<ITenantSubscriptionService, TenantSubscriptionService>();
builder.Services.AddHostedService<PaymentGraceReminderService>();
builder.Services.AddSingleton<IR2StorageService, CloudflareR2StorageService>();

Stripe.StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

// Admin requests are signed in through Clerk: the admin portal sends the Clerk session token as a
// Bearer token, and the admin user ID comes from its sub claim (see AdminUserHttpContextExtensions).
var clerkOptions = builder.Configuration.GetSection(ClerkAuthOptions.SectionName).Get<ClerkAuthOptions>()
    ?? new ClerkAuthOptions();
var clerkIssuer = clerkOptions.Issuer.Trim().TrimEnd('/');
if (string.IsNullOrEmpty(clerkIssuer))
{
    throw new InvalidOperationException(
        "Clerk:Issuer must be set to the Clerk Frontend API URL so admin sign-in tokens can be checked.");
}

var clerkAuthorizedParties = clerkOptions.AuthorizedParties
    .Where(party => !string.IsNullOrWhiteSpace(party))
    .Select(party => party.Trim().TrimEnd('/'))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Signing keys come from Clerk's published key set, found via {issuer}/.well-known/openid-configuration.
        options.Authority = clerkIssuer;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = clerkIssuer,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            // Clerk session tokens carry no audience claim.
            ValidateAudience = false,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (clerkAuthorizedParties.Count == 0)
                {
                    return Task.CompletedTask;
                }

                var authorizedParty = context.Principal?.FindFirst("azp")?.Value?.TrimEnd('/');
                if (string.IsNullOrEmpty(authorizedParty) || !clerkAuthorizedParties.Contains(authorizedParty))
                {
                    context.Fail("The token was not issued for an authorized origin.");
                }

                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("ClerkAuthentication")
                    .LogWarning("Clerk token rejected for {Method} {Path}: {Reason}",
                        context.Request.Method, context.Request.Path, context.Exception.Message);
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DbInitializer.InitializeAsync(dbContext, CancellationToken.None);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        if (exceptionFeature?.Error is not null)
        {
            logger.LogError(exceptionFeature.Error, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
    });
});

app.UseCors();

app.UseAuthentication();

app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
