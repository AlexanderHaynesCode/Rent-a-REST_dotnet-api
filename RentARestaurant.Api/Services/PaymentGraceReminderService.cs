using Microsoft.EntityFrameworkCore;
using RentARestaurant.Api.Data;
using RentARestaurant.Api.Infrastructure.Email;

namespace RentARestaurant.Api.Services;

// Stripe sends no event for "suspension is near", so a timer sends the reminder and re-syncs overdue grace periods.
public sealed class PaymentGraceReminderService(
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentGraceReminderService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReminderWindow = TimeSpan.FromHours(48);
    private static readonly TimeSpan OverdueSyncDelay = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Payment grace reminder pass failed.");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var tenantSubscriptionService = scope.ServiceProvider.GetRequiredService<ITenantSubscriptionService>();

        var now = DateTime.UtcNow;
        var reminderCutoff = now.Add(ReminderWindow);
        var overdueCutoff = now.Subtract(OverdueSyncDelay);

        var due = await dbContext.TenantSubscriptions.IgnoreQueryFilters()
            .Where(x => x.Status == "past_due" && x.GraceEndsUtc != null)
            .Where(x => (x.GraceReminderSentUtc == null && x.GraceEndsUtc <= reminderCutoff && x.GraceEndsUtc > now)
                || x.GraceEndsUtc <= overdueCutoff)
            .ToListAsync(cancellationToken);

        foreach (var row in due)
        {
            try
            {
                if (row.GraceEndsUtc <= overdueCutoff)
                {
                    // Safety net in case Stripe's unpaid transition webhook was missed.
                    await tenantSubscriptionService.SyncAsync(row.StripeSubscriptionId, cancellationToken);
                    continue;
                }

                var tenant = await dbContext.Tenants.IgnoreQueryFilters()
                    .SingleOrDefaultAsync(x => x.Id == row.TenantId, cancellationToken);
                var ownerEmail = await dbContext.TenantUsers.IgnoreQueryFilters()
                    .Where(x => x.TenantId == row.TenantId)
                    .OrderBy(x => x.Role == "Owner" ? 0 : 1)
                    .Select(x => x.Email)
                    .FirstOrDefaultAsync(cancellationToken);

                if (tenant is not null && !string.IsNullOrWhiteSpace(ownerEmail))
                {
                    await emailService.SendPaymentGraceReminderEmailAsync(ownerEmail, tenant.Name, row.GraceEndsUtc!.Value, cancellationToken);
                }

                // Marked even without a recipient so a tenant without an owner email is not retried hourly.
                row.GraceReminderSentUtc = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Payment grace handling failed for tenant {TenantId}", row.TenantId);
            }
        }
    }
}
