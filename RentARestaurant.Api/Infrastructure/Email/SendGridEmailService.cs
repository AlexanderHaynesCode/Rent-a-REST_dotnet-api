using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace RentARestaurant.Api.Infrastructure.Email;

public class SmtpEmailService(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    private const string AdminUrl = "admin.rentaurants.com";  //"https://rent-a-rest-admin-client-app.pages.dev/login";
    private const string RestaurantBaseUrl = "menu.rentaurants.com";  // "https://rent-a-rest-restaurant-client-app.pages.dev";

    public async Task SendWelcomeEmailAsync(string toEmail, string restaurantName, string slug, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var restaurantUrl = $"{RestaurantBaseUrl}/{slug}";

        using var client = new SmtpClient(opts.Host, opts.Port)
        {
            EnableSsl = opts.EnableSsl,
            Credentials = new NetworkCredential(opts.Username, opts.Password)
        };

        using var message = new MailMessage
        {
            From = new MailAddress(opts.FromAddress, opts.FromName),
            Subject = $"Welcome to Rent-a-RESTaurant \u2014 {restaurantName} is live!",
            IsBodyHtml = true,
            Body = $"""
                <div style="font-family:sans-serif;max-width:600px;margin:0 auto;">
                  <h2>Welcome to Rent-a-RESTaurant!</h2>
                  <p>Your restaurant <strong>{restaurantName}</strong> has been successfully provisioned.</p>
                  <p>Here are your links:</p>
                  <ul>
                    <li><strong>Admin Dashboard:</strong> <a href="{AdminUrl}">{AdminUrl}</a></li>
                    <li><strong>Your Restaurant Page:</strong> <a href="{restaurantUrl}">{restaurantUrl}</a></li>
                  </ul>
                  <p>Welcome aboard!</p>
                  <p>The Rent-a-RESTaurant Team</p>
                </div>
                """
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message, cancellationToken);
        logger.LogInformation("Welcome email sent to {Email} for restaurant {Slug}", toEmail, slug);
    }

    public async Task SendAgentChangesAppliedEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> appliedSummary,
        Guid auditId,
        DateTime rollbackExpiresUtc,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;

        using var client = new SmtpClient(opts.Host, opts.Port)
        {
            EnableSsl = opts.EnableSsl,
            Credentials = new NetworkCredential(opts.Username, opts.Password)
        };

        var summaryHtml = string.Join(string.Empty, appliedSummary.Select(line => $"<li>{System.Net.WebUtility.HtmlEncode(line)}</li>"));

        using var message = new MailMessage
        {
            From = new MailAddress(opts.FromAddress, opts.FromName),
            Subject = $"Your website has been updated \u2014 {restaurantName}",
            IsBodyHtml = true,
            Body = $"""
                <div style="font-family:sans-serif;max-width:600px;margin:0 auto;">
                  <h2>Your website updates are live!</h2>
                  <p>We applied the following changes to <strong>{restaurantName}</strong> automatically:</p>
                  <ul>
                    {summaryHtml}
                  </ul>
                  <p>If something doesn't look right, reply to this email and we'll help, or these changes can be
                  rolled back until <strong>{rollbackExpiresUtc:yyyy-MM-dd}</strong> (reference: {auditId}).</p>
                  <p>The Rent-a-RESTaurant Team</p>
                </div>
                """
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message, cancellationToken);
        logger.LogInformation("Agent-applied-changes email sent to {Email} for audit {AuditId}", toEmail, auditId);
    }

    public async Task SendAgentClarificationNeededEmailAsync(
        string toEmail,
        string restaurantName,
        IReadOnlyList<string> clarificationQuestions,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;

        using var client = new SmtpClient(opts.Host, opts.Port)
        {
            EnableSsl = opts.EnableSsl,
            Credentials = new NetworkCredential(opts.Username, opts.Password)
        };

        var questionsHtml = string.Join(string.Empty, clarificationQuestions.Select(q => $"<li>{System.Net.WebUtility.HtmlEncode(q)}</li>"));

        using var message = new MailMessage
        {
            From = new MailAddress(opts.FromAddress, opts.FromName),
            Subject = $"We need a bit more info to update your website \u2014 {restaurantName}",
            IsBodyHtml = true,
            Body = $"""
                <div style="font-family:sans-serif;max-width:600px;margin:0 auto;">
                  <h2>Almost there!</h2>
                  <p>We received your update request for <strong>{restaurantName}</strong>, but need clarification before applying it:</p>
                  <ul>
                    {questionsHtml}
                  </ul>
                  <p>Just reply to this email (or your original message) with the details, and we'll take care of the rest.</p>
                  <p>The Rent-a-RESTaurant Team</p>
                </div>
                """
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message, cancellationToken);
        logger.LogInformation("Agent-clarification-needed email sent to {Email}", toEmail);
    }
}
