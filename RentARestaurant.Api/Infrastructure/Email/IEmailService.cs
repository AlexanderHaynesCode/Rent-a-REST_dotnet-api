namespace RentARestaurant.Api.Infrastructure.Email;

public interface IEmailService
{
    Task SendWelcomeEmailAsync(string toEmail, string restaurantName, string slug, CancellationToken cancellationToken);

    Task SendAgentChangesAppliedEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> appliedSummary,
        Guid auditId,
        DateTime rollbackExpiresUtc,
        CancellationToken cancellationToken);

    Task SendAgentClarificationNeededEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> clarificationQuestions,
        CancellationToken cancellationToken);
}
