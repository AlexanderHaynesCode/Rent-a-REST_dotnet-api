namespace RentARestaurant.Api.Infrastructure.Email;

public interface IEmailService
{
    Task SendWelcomeEmailAsync(string toEmail, string restaurantName, string slug, string subscriptionPlan, CancellationToken cancellationToken);

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

    Task SendAgentClarificationNeededEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> clarificationQuestions,
        CancellationToken cancellationToken);
}
