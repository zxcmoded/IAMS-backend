namespace IAMS.Api.Common.Domain;

// F4 — Inventory Operations (MVP, offline-first). Every mutable row that mobile commits locally and later
// pushes carries the two things offline replay needs: an application-managed monotonic version so a queued
// mutation can be re-evaluated against current server state (conflict detection), and — on the ledger and
// count rows the client authors — a client IdempotencyKey so a replayed push is de-duplicated, not
// double-applied. See phase-2a-inventory-scanning.md for the full rationale.

/// <summary>
/// SKU / inventory-item master, tenant-scoped and owned by a single <see cref="Company"/>. This is
/// master data the mobile client pulls once and reads offline, so it carries the same
/// <c>SyncCursorUtc</c> keyset column as the physical hierarchy (see master-data-sync.md). Scanning (F3)
/// resolves a raw code to one of these by <see cref="Sku"/> or <see cref="Barcode"/> within the caller's
/// reachable company set.
/// </summary>
public class InventoryItem
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>Human/business SKU code. Unique per company (a code can repeat across companies).</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>Optional scannable barcode distinct from the SKU; also a scan-resolution key.</summary>
    public string? Barcode { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Unit of measure label (e.g. "EA", "KG"). Free text — no controlled vocabulary imposed yet.</summary>
    public string? UnitOfMeasure { get; set; }

    /// <summary>Optional category tag; feeds the existing <see cref="ConnectionFilterType.Category"/> filter dimension.</summary>
    public string? Category { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<StockLevel> StockLevels { get; set; } = new List<StockLevel>();
}

/// <summary>
/// The on-hand quantity of one <see cref="InventoryItem"/> in one <see cref="Bin"/> — the single source of
/// truth every transaction mutates. Denormalizes the bin's full ancestor chain (mirroring <see cref="Bin"/>)
/// so tenant-isolation / cross-tenant scope checks and company-scoped reads are direct indexed comparisons,
/// not a join up the hierarchy on the hot path.
///
/// Two independent concurrency mechanisms guard it (mirroring the connection model's PolicyRevision-vs-xmin
/// split):
/// <list type="bullet">
/// <item><see cref="Version"/> — application-incremented on every applied movement. The mobile stamps a
/// queued mutation with the version it observed at offline-commit time; at push the backend compares it to
/// the current value and rejects (409) a mutation built on a stale base — this is the offline
/// commit-then-server-conflict detector.</item>
/// <item>the Postgres <c>xmin</c> row-version (configured in <c>IamsDbContext.OnModelCreating</c>, Npgsql
/// only) — turns two concurrent server-side applies to the same row into a catchable
/// <c>DbUpdateConcurrencyException</c> for the loser instead of a lost update.</item>
/// </list>
/// <see cref="QuantityOnHand"/> carries a <c>&gt;= 0</c> CHECK so a transfer/adjustment can never drive a
/// source bin negative at the storage layer even if application logic were bypassed.
/// </summary>
public class StockLevel
{
    public Guid Id { get; set; }

    public Guid InventoryItemId { get; set; }
    public InventoryItem InventoryItem { get; set; } = null!;

    public Guid BinId { get; set; }
    public Bin Bin { get; set; } = null!;

    // Denormalized ancestry (write-once with the bin; matches Bin's own chain).
    public Guid RackId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid LocationId { get; set; }
    public Guid CompanyId { get; set; }

    public decimal QuantityOnHand { get; set; }

    /// <summary>Application-managed monotonic counter; bumped in the same transaction as each applied movement.</summary>
    public long Version { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Append-only inventory movement ledger — one row per receive / transfer / adjustment (F4;
/// BR-013 receive/transfer, BR-009/010 adjustments). The ledger is never mutated to undo a movement: a
/// correction is a new offsetting <see cref="InventoryTransactionType.Adjustment"/> that references the
/// original via <see cref="ReversalOfTransactionId"/>. Which bin columns are set, and the sign/positivity
/// of <see cref="Quantity"/>, are constrained per <see cref="TransactionType"/> by table CHECKs.
///
/// The <see cref="Id"/> may be client-generated offline; <see cref="IdempotencyKey"/> (unique) is what
/// actually de-duplicates a replayed push, independent of the id. <see cref="BaseSourceStockVersion"/> /
/// <see cref="BaseDestinationStockVersion"/> capture the <see cref="StockLevel.Version"/> the client relied
/// on at commit time, so the backend can detect a conflicting concurrent write when the queued mutation
/// finally arrives.
/// </summary>
public class InventoryTransaction
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid InventoryItemId { get; set; }
    public InventoryItem InventoryItem { get; set; } = null!;

    public InventoryTransactionType TransactionType { get; set; }

    /// <summary>Source bin — set for Transfer (moved-from) and Adjustment (adjusted bin); null for Receive.</summary>
    public Guid? SourceBinId { get; set; }
    public Bin? SourceBin { get; set; }

    /// <summary>Destination bin — set for Receive (received-into) and Transfer (moved-to); null for Adjustment.</summary>
    public Guid? DestinationBinId { get; set; }
    public Bin? DestinationBin { get; set; }

    /// <summary>
    /// Movement magnitude. For Receive/Transfer a positive amount moved; for Adjustment the SIGNED delta
    /// (may be negative, never zero). Domain constraints per type are enforced by table CHECKs.
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>Required for Adjustment (BR-009/010); null otherwise.</summary>
    public string? AdjustmentReason { get; set; }

    public InventoryTransactionStatus Status { get; set; } = InventoryTransactionStatus.Applied;

    /// <summary>Populated when <see cref="Status"/> is <see cref="InventoryTransactionStatus.Rejected"/>.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>Client-generated de-duplication key; a replayed offline push with the same key is a no-op.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary><see cref="StockLevel.Version"/> observed for the source bin at offline-commit time (conflict detection).</summary>
    public long? BaseSourceStockVersion { get; set; }

    /// <summary><see cref="StockLevel.Version"/> observed for the destination bin at offline-commit time.</summary>
    public long? BaseDestinationStockVersion { get; set; }

    /// <summary>When this is a correcting adjustment, the original transaction it reverses.</summary>
    public Guid? ReversalOfTransactionId { get; set; }
    public InventoryTransaction? ReversalOf { get; set; }

    public string? DeviceId { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    /// <summary>When the mutation was committed on the device (offline); distinct from server <see cref="CreatedAtUtc"/>.</summary>
    public DateTime? ClientCreatedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// A single counted observation of one item in one bin (F4, BR-014). <see cref="SystemQuantity"/> snapshots
/// the computed on-hand at count time and <see cref="Variance"/> is the server-computed
/// <c>CountedQuantity - SystemQuantity</c> (a Postgres STORED generated column under Npgsql; a plain
/// writable property under the in-memory test provider — same technique as the sync cursor).
///
/// The threshold that decides whether the count auto-applies or needs approval is NOT hardcoded: it is read
/// from <see cref="InventorySettings"/> and SNAPSHOTTED here (<see cref="VarianceThreshold"/> +
/// <see cref="VarianceThresholdType"/>) so a later config change never retroactively re-classifies a count
/// that was already adjudicated. On approval the reconciling movement is written to the ledger and linked
/// via <see cref="AdjustmentTransactionId"/>.
/// </summary>
public class StockCount
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid InventoryItemId { get; set; }
    public InventoryItem InventoryItem { get; set; } = null!;

    public Guid BinId { get; set; }
    public Bin Bin { get; set; } = null!;

    // Denormalized ancestry (CompanyId above completes the chain).
    public Guid RackId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid LocationId { get; set; }

    public decimal CountedQuantity { get; set; }

    /// <summary>Computed on-hand snapshot at the moment of counting (basis for the variance).</summary>
    public decimal SystemQuantity { get; set; }

    /// <summary>Server-computed <c>CountedQuantity - SystemQuantity</c> (STORED generated column under Npgsql).</summary>
    public decimal Variance { get; set; }

    /// <summary>Threshold value in force at count time (snapshot; source-configurable, never hardcoded).</summary>
    public decimal VarianceThreshold { get; set; }
    public VarianceThresholdType VarianceThresholdType { get; set; }

    public StockCountStatus Status { get; set; }

    public Guid CountedByUserId { get; set; }
    public User CountedByUser { get; set; } = null!;

    public Guid? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectionReason { get; set; }

    /// <summary>The reconciling ledger adjustment written when an over-threshold count is approved.</summary>
    public Guid? AdjustmentTransactionId { get; set; }
    public InventoryTransaction? AdjustmentTransaction { get; set; }

    /// <summary>Client-generated de-duplication key for offline replay.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary><see cref="StockLevel.Version"/> observed at count time (conflict detection at push).</summary>
    public long? BaseStockVersion { get; set; }

    public string? DeviceId { get; set; }
    public DateTime? ClientCreatedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Per-company inventory configuration. Holds the variance threshold (value + interpretation) used to
/// decide when a <see cref="StockCount"/> needs approval, so that value is data — not a constant in code.
/// One row per company (unique <see cref="CompanyId"/>). Config edits ride the Postgres <c>xmin</c>
/// optimistic-concurrency token (configured in <c>IamsDbContext.OnModelCreating</c>).
/// </summary>
public class InventorySettings
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public decimal VarianceThreshold { get; set; }
    public VarianceThresholdType VarianceThresholdType { get; set; } = VarianceThresholdType.AbsoluteQuantity;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
