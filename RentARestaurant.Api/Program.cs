using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Infrastructure.Storage;
using RentARestaurant.Api.Infrastructure.Stripe;
using RentARestaurant.Api.Infrastructure.Tenancy;
using RentARestaurant.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TenantResolutionOptions>(
    builder.Configuration.GetSection(TenantResolutionOptions.SectionName));
builder.Services.Configure<LocalMediaStorageOptions>(
    builder.Configuration.GetSection(LocalMediaStorageOptions.SectionName));
builder.Services.Configure<StripeOptions>(
    builder.Configuration.GetSection(StripeOptions.SectionName));

// builder.Services.AddDbContext<AppDbContext>(options =>
//     options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQLConnection")));

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
builder.Services.AddScoped<IMediaNamespaceProvisioner, LocalMediaNamespaceProvisioner>();
builder.Services.AddScoped<ITenantAccessService, TenantAccessService>();

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

app.UseCors();

app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
