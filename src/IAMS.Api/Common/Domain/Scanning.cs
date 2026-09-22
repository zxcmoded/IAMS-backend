namespace IAMS.Api.Common.Domain;

/// <summary>
/// F3 — an audit record of one scan: who scanned, the raw code, what it resolved to, and the tenant/company
/// scope the scan happened in. Append-only.
///
/// BR-003 (tenant isolation on resolve) is expressed here by <see cref="ScanResolvedType.Blocked"/>: when a
/// raw code matches a real entity that belongs to a tenant/company outside the scanner's reachable set, the
/// resolver records <see cref="ResolvedType"/> = Blocked and leaves <see cref="ResolvedEntityId"/> NULL — the
/// other tenant's entity id is never written, so the scan log itself cannot leak it. A code that matches
/// nothing is <see cref="ScanResolvedType.NoMatch"/> (also null id); the two are deliberately distinct so
/// "belongs to someone else" is never reported as "doesn't exist" or vice-versa.
///
/// <see cref="ResolvedEntityId"/> is intentionally a bare <c>Guid?</c> with NO foreign key: the target is
/// polymorphic (an <see cref="InventoryItem"/>, a <see cref="Location"/>/<see cref="Bin"/>, or — from F5 — a
/// Fixed Asset, whose table does not exist yet). The routing target is identified by <see cref="ResolvedType"/>
/// plus the id; a single hard FK cannot span the alternatives, and the Asset arm has nothing to point at
/// today. This is a log row, not a relational parent of those entities, so the missing FK is by design.
/// </summary>
public class ScanEvent
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ScannedByUserId { get; set; }
    public User ScannedByUser { get; set; } = null!;

    /// <summary>The raw string the scanner captured, before resolution.</summary>
    public string RawCode { get; set; } = string.Empty;

    public ScanResolvedType ResolvedType { get; set; }

    /// <summary>
    /// Id of the resolved entity when <see cref="ResolvedType"/> is Sku/Location/Asset; NULL for NoMatch and
    /// (deliberately, BR-003) Blocked. No FK — see the type remarks.
    /// </summary>
    public Guid? ResolvedEntityId { get; set; }

    public string? DeviceId { get; set; }

    /// <summary>When the scan happened on the device (may be offline); distinct from server <see cref="CreatedAtUtc"/>.</summary>
    public DateTime ScannedAtUtc { get; set; }

    /// <summary>Optional client de-duplication key so a replayed queued scan-event isn't logged twice.</summary>
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
