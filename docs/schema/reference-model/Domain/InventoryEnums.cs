namespace IAMS.Api.Common.Domain;

// Phase 2a (F3 Scanning, F4 Inventory Operations) enums. Same conventions as the Phase-1 enums
// (Enums.cs): int-backed for stable in-memory values, persisted as strings via HasConversion<string>()
// + a CHECK … IN (…) domain constraint so a reordering can never silently repurpose a stored value.

/// <summary>
/// The kind of inventory movement one ledger row represents (F4). Which bin columns are populated and the
/// sign/positivity of <c>Quantity</c> are constrained per type by a table CHECK (see
/// <c>InventoryTransactionConfiguration</c>):
/// <list type="bullet">
/// <item><see cref="Receive"/> — into <c>DestinationBin</c> only, <c>Quantity &gt; 0</c> (BR-013).</item>
/// <item><see cref="Transfer"/> — <c>SourceBin</c> → <c>DestinationBin</c> (distinct bins), <c>Quantity &gt; 0</c> (BR-013).</item>
/// <item><see cref="Adjustment"/> — signed delta against <c>SourceBin</c>, <c>Quantity &lt;&gt; 0</c>, reason required (BR-009/010).</item>
/// </list>
/// </summary>
public enum InventoryTransactionType
{
    Receive = 1,
    Transfer = 2,
    Adjustment = 3,
}

/// <summary>
/// Outcome recorded on a ledger row. The ledger is append-only: a correction is a NEW offsetting
/// <see cref="InventoryTransactionType.Adjustment"/> that points back via
/// <c>InventoryTransaction.ReversalOfTransactionId</c> — existing rows are never mutated to "undo" them.
/// <see cref="Rejected"/> exists so a queued offline push that fails server-side re-evaluation (stale
/// base version, over-source, invalid destination, lost cross-tenant permission) can be persisted for the
/// F11 sync-queue UI to surface, rather than vanishing — the backend MAY record it instead of only
/// returning 409.
/// </summary>
public enum InventoryTransactionStatus
{
    Applied = 1,
    Rejected = 2,
}

/// <summary>
/// Lifecycle of a stock count (F4, BR-014). A count whose absolute variance is within the effective
/// threshold is applied straight away (<see cref="Completed"/>); one that exceeds the threshold is parked
/// as <see cref="PendingApproval"/> until an approver <see cref="Approved"/> it (which generates the
/// reconciling adjustment) or <see cref="Rejected"/> it (no stock change).
/// </summary>
public enum StockCountStatus
{
    Completed = 1,
    PendingApproval = 2,
    Approved = 3,
    Rejected = 4,
}

/// <summary>
/// How a configured variance threshold is interpreted. Kept open (not hardcoded to one meaning) because
/// the threshold's units are unspecified upstream: an absolute counted-unit delta, or a percentage of the
/// system quantity. The <c>InventorySettings</c> row carries both the value and this discriminator, and
/// each <c>StockCount</c> snapshots the pair in force at count time.
/// </summary>
public enum VarianceThresholdType
{
    AbsoluteQuantity = 1,
    Percentage = 2,
}

/// <summary>
/// What a scanned raw code resolved to (F3). <see cref="Blocked"/> is the BR-003 tenant-isolation outcome:
/// the code DID match a real entity, but one outside the scanner's reachable tenant/company set — the
/// resolver returns Blocked and stores NO <c>ResolvedEntityId</c>, so a cross-tenant code can never leak
/// the existence or id of another tenant's entity. <see cref="Asset"/> is a routing tag only for now — the
/// Fixed Assets table lands in F5, so there is no FK for it yet (see <c>ScanEvent.ResolvedEntityId</c>).
/// </summary>
public enum ScanResolvedType
{
    Sku = 1,
    Location = 2,
    Asset = 3,
    NoMatch = 4,
    Blocked = 5,
}
