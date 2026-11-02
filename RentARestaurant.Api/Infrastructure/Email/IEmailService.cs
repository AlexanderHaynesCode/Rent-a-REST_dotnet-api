namespace RentARestaurant.Api.Infrastructure.Email;

public interface IEmailService
{
    Task SendWelcomeEmailAsync(string toEmail, string restaurantName, string slug, string subscriptionPlan, CancellationToken cancellationToken);

    Task SendWelcomeBackEmailAsync(string toEmail, string restaurantName, string slug, string subscriptionPlan, CancellationToken cancellationToken);

    Task SendDoneForYouUpgradeEmailAsync(string toEmail, string restaurantName, CancellationToken cancellationToken);

    Task SendPaymentFailedEmailAsync(string toEmail, string restaurantName, DateTime graceEndsUtc, CancellationToken cancellationToken);

    Task SendPaymentGraceReminderEmailAsync(string toEmail, string restaurantName, DateTime graceEndsUtc, CancellationToken cancellationToken);

    Task SendAccountSuspendedEmailAsync(string toEmail, string restaurantName, CancellationToken cancellationToken);

    Task SendPaymentRecoveredEmailAsync(string toEmail, string restaurantName, string slug, CancellationToken cancellationToken);

    Task SendAgentChangesAppliedEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> appliedSummary,
        IReadOnlyList<string> rejectedSummary,
        Guid auditId,
        DateTime rollbackExpiresUtc,
        CancellationToken cancellationToken);

    Task SendAgentChangesNotAppliedEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> rejectedSummary,
        CancellationToken cancellationToken);

    Task SendAgentRollbackAppliedEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> revertedSummary,
        DateTime restoredToBeforeUtc,
        CancellationToken cancellationToken);

    Task SendAgentRollbackNotAppliedEmailAsync(
        string toEmail,
        string restaurantName,
        string reason,
        CancellationToken cancellationToken);

    Task SendAgentRateLimitedEmailAsync(
        string toEmail,
        string restaurantName,
        int maxPerHour,
        CancellationToken cancellationToken);

    Task SendAgentClarificationNeededEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> clarificationQuestions,
        CancellationToken cancellationToken);
}
