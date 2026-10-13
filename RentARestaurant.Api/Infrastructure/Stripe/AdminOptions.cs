namespace RentARestaurant.Api.Infrastructure.Stripe;

public class AdminOptions
{
    public const string SectionName = "Admin";

    // Origins the admin app may be served from; Stripe return URLs must match one of these.
    public string[] AllowedReturnOrigins { get; set; } = [];
}
