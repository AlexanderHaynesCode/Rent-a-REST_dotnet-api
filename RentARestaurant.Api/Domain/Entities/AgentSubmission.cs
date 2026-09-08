using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

public enum AgentSubmissionStatus
{
    Received, // 0 in PostgreSQL for 'status' column in AgentSubmission table
    Translated, // 1
    Validated, // 2
    NeedsClarification, // 3
    Applied, // 4
    Rejected, // 5
    Failed // 6
}

/// <summary>
/// A single inbound message from a tenant (email, and later SMS/MMS) that is being processed
/// by the AI agent pipeline (intake -> validator -> executor).
/// </summary>
public class AgentSubmission : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Provider-agnostic intake channel, e.g. "email" or "sms".</summary>
    public string Channel { get; set; } = "email";

    /// <summary>Email address or phone number the message came from.</summary>
    public string SenderIdentifier { get; set; } = string.Empty;

    public string? RawBodyText { get; set; }

    /// <summary>JSON array of attachment references (URLs/keys in storage).</summary>
    public string? AttachmentRefs { get; set; }

    public AgentSubmissionStatus Status { get; set; } = AgentSubmissionStatus.Received;

    /// <summary>Structured JSON produced by the translator LLM (Groq/Llama).</summary>
    public string? TranslatedJson { get; set; }

    /// <summary>Confidence score (0-1) produced by the validator LLM (Claude Haiku).</summary>
    public double? ValidatorConfidence { get; set; }

    public string? RejectionReason { get; set; }

    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
