namespace RentARestaurant.Api.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string ApiKey { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Rent-a-RESTaurant";
}
