using RentARestaurant.Api.Domain;

namespace RentARestaurant.Api.Domain.Entities;

/// <summary>
/// Records the before-state and applied diff for a single agent-driven change so it can be
/// rolled back within the retention window.
/// </summary>
public class AgentChangeAudit : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SubmissionId { get; set; }

    /// <summary>Full JSON snapshot of the restaurant (menu/hours/branding) before the change.</summary>
    public string PreChangeSnapshotJson { get; set; } = string.Empty;

    /// <summary>Human-readable list of applied changes, serialized as a JSON string array.</summary>
    public string DiffSummaryJson { get; set; } = string.Empty;

    public DateTime AppliedUtc { get; set; } = DateTime.UtcNow;
    public DateTime RollbackExpiresUtc { get; set; }
    public bool RolledBack { get; set; }
    public DateTime? RolledBackUtc { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public AgentSubmission Submission { get; set; } = null!;
}
