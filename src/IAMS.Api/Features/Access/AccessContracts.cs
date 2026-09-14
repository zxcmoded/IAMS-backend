using IAMS.Api.Common.Access;

namespace IAMS.Api.Features.Access;

/// <summary>
/// The hierarchy coordinates of a resource the client wants to act on. In Phase 1 there are no
/// asset/inventory tables yet, so the client supplies the coordinates (it knows them from the
/// scanned/queued resource) and the server resolves the owning tenant from <see cref="CompanyId"/> and
/// evaluates connection policy. <see cref="ResourceType"/>/<see cref="ResourceId"/> are opaque labels
/// echoed back for correlation, not enforced yet.
/// </summary>
public record ResourceRef(
    Guid CompanyId,
    Guid? LocationId = null,
    Guid? WarehouseId = null,
    Guid? RackId = null,
    Guid? BinId = null,
    string? ResourceType = null,
    string? ResourceId = null);

/// <summary>
/// The decision returned for one evaluated resource. Shape is identical for single and batch.
/// <see cref="EffectivePermission"/> is a string so a denial with no grant serializes as "None"
/// (the stored permission enum only has Read/Write/Full).
/// </summary>
public record AccessDecisionResponse(
    string Decision,
    string EffectivePermission,
    AccessReason Reason,
    Guid? ConnectionId,
    long? PolicyVersion)
{
    public static AccessDecisionResponse From(AccessDecision d) => new(
        d.Allowed ? "Allowed" : "Denied",
        d.EffectivePermission?.ToString() ?? "None",
        d.Reason,
        d.ConnectionId,
        d.PolicyVersion);
}
