using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace RentARestaurant.Api.Infrastructure.Email;

public class SmtpEmailService(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    private const string AdminUrl = "https://rent-a-rest-admin-client-app.pages.dev/login";
    private const string RestaurantBaseUrl = "https://rent-a-rest-restaurant-client-app.pages.dev";

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
}
