namespace RentARestaurant.Api.Infrastructure.Tenancy;

/// <summary>
/// Settings for checking the Clerk session token the admin portal sends as a Bearer token.
/// </summary>
public sealed class ClerkAuthOptions
{
    public const string SectionName = "Clerk";

    /// <summary>
    /// The Clerk Frontend API URL (Clerk dashboard, API keys), e.g. https://clerk.example.com.
    /// Tokens must be issued by it, and its published signing keys are used to check them.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Origins allowed in the token's azp claim (the admin portal's URLs). Empty skips the check.
    /// </summary>
    public string[] AuthorizedParties { get; set; } = [];
}
