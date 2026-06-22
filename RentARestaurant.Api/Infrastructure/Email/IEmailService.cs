namespace RentARestaurant.Api.Infrastructure.Email;

public interface IEmailService
{
    Task SendWelcomeEmailAsync(string toEmail, string restaurantName, string slug, CancellationToken cancellationToken);
}
