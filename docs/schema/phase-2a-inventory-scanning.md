# IAMS Backend — Schema Design (Phase 2a)

**Scope:** F3 (Scanning) and F4 (Inventory Operations, MVP, offline-first). Builds **on top of** the Phase-1
model in `docs/schema/README.md` (Tenant→Company→Location→Warehouse→Rack→Bin hierarchy, denormalized
ancestry, `PolicyRevision`/`xmin` concurrency, string-enum + CHECK, `SyncCursorUtc` keyset sync). Nothing in
Phase 1 is duplicated or altered — these are new tables that reference the existing hierarchy and reuse its
conventions.

**Status:** Design + EF Core reference model (entities + configurations), targeting EF Core 10 / Npgsql on
PostgreSQL. Reference code lives in `reference-model/Domain/{Inventory,Scanning,InventoryEnums}.cs` and
`reference-model/Persistence/InventoryConfigurations.cs`, written against the **real `src/IAMS.Api`
conventions** (namespaces `IAMS.Api.Common.Domain` / `…Persistence.Configurations`, Postgres double-quoted
identifiers, `now()`, `SyncCursor.ColumnName`, `AddEnumCheck<T>()`) so the `dotnet-backend-engineer` can drop
them straight into `src` and generate one migration. **Not yet compiled/migrated here** — see §7.

> This is a schema deliverable only. API endpoints (scan resolve, receive/transfer/count/adjust, idempotent
> replay, 409 conflict signalling) are the `dotnet-backend-engineer`'s next task, working from this schema.

---

## 1. What this covers

| Feature | Tables |
|---|---|
| F4 — SKU master | `InventoryItems` |
| F4 — per-bin stock | `StockLevels` |
| F4 — movement ledger | `InventoryTransactions` (receive / transfer / adjustment) |
| F4 — stock count / variance | `StockCounts`, `InventorySettings` (configurable threshold) |
| F3 — scanning | `ScanEvents` |

New enums (`InventoryEnums.cs`): `InventoryTransactionType`, `InventoryTransactionStatus`,
`StockCountStatus`, `VarianceThresholdType`, `ScanResolvedType`.

Target `src` paths for the backend engineer:

```text
reference-model/Domain/Inventory.cs            → src/IAMS.Api/Common/Domain/Inventory.cs
reference-model/Domain/Scanning.cs             → src/IAMS.Api/Common/Domain/Scanning.cs
reference-model/Domain/InventoryEnums.cs       → src/IAMS.Api/Common/Domain/InventoryEnums.cs
reference-model/Persistence/InventoryConfigurations.cs
                                               → src/IAMS.Api/Common/Persistence/Configurations/InventoryConfigurations.cs
```

`ApplyConfigurationsFromAssembly` already auto-registers the new `IEntityTypeConfiguration<>` classes — no
manual registration. The DbSets + Npgsql-only wiring in §5 are the only `IamsDbContext` edits.

---

## 2. Key design decisions (and why)

### 2.1 Stock level is the single mutable source of truth; the ledger is append-only
`StockLevels` (one row per `(InventoryItemId, BinId)`, unique) holds the authoritative on-hand quantity.
`InventoryTransactions` is an **append-only ledger** — every receive/transfer/adjustment is one immutable
row, and a mutation applies its effect to the relevant `StockLevels` row(s) in the same transaction. A
correction is **never** an in-place edit of a past ledger row; it's a new offsetting `Adjustment` that points
back via `ReversalOfTransactionId`. This keeps the ledger a truthful, auditable history and localizes all
concurrency to the small, hot `StockLevels` row.

### 2.2 Offline replay needs two distinct guards — idempotency **and** versioning (they are not the same)
Mobile commits locally, then pushes a queued mutation later. Two independent failure modes, two mechanisms:

- **Double-apply of the *same* mutation** (client retries a push it already delivered) → **`IdempotencyKey`**
  (unique, client-generated, on `InventoryTransactions` and `StockCounts`). A replay with a key already
  present is a no-op returning the original result. The key is independent of the row `Id` (which may itself
  be client-generated offline).
- **Conflicting *concurrent* write** (someone else changed the bin while the mutation sat in the outbox) →
  **`StockLevel.Version`** (application-managed, monotonic, bumped on every applied movement). The client
  stamps a queued mutation with the version it observed at commit time (`BaseSourceStockVersion` /
  `BaseDestinationStockVersion` on transactions, `BaseStockVersion` on counts). At push the backend compares
  stamped-vs-current and, on mismatch, returns the 409 conflict signal instead of blindly applying. This is
  the direct analogue of Phase-1's `PolicyRevision` staleness stamp (README §2.4).

`StockLevels` **also** carries the Postgres `xmin` row-version (§5) — a lower-level guard against two
*server-side* applies racing on the same row within the DB, turning the loser into a catchable
`DbUpdateConcurrencyException`. `Version` is the client-facing semantic version the mobile can read and
stamp; `xmin` is the internal last-writer-wins interlock. Same split as `PolicyRevision`-vs-`xmin` on
connections — deliberate, not redundant.

### 2.3 Per-type shape enforced by CHECK, not just by application code (the edge cases)
One ledger table serves three movement types; a single `CK_InvTxn_TypeShape` CHECK makes the illegal shapes
**unrepresentable at the storage layer**:

| Type | SourceBin | DestinationBin | Quantity | Reason |
|---|---|---|---|---|
| `Receive` | NULL | **required** | `> 0` | — |
| `Transfer` | **required** | **required**, `<>` source | `> 0` | — |
| `Adjustment` | **required** | NULL | `<> 0` (signed) | **required** (`CK_InvTxn_AdjustmentReason`, BR-009/010) |

Plus `CK_StockLevels_NonNegative` (`QuantityOnHand >= 0`) so a transfer/over-source or a negative adjustment
**cannot drive a source bin negative even if application logic were bypassed** — the DB rejects it. This
covers the "negative / over-source qty on transfer" and "invalid destination" edge cases at two layers
(handler pre-check for a clean 4xx, CHECK as the backstop). "Invalid destination" that isn't a real bin is
caught by the `DestinationBinId` FK; `Transfer` with source == destination is caught by the CHECK.

### 2.4 Variance is a computed column; the threshold is configurable data, never hardcoded
`StockCounts.Variance` is a **STORED generated column** `CountedQuantity - SystemQuantity` (§5) — one
authoritative definition, impossible to write an inconsistent value. `SystemQuantity` snapshots computed
on-hand at count time so the variance is reproducible after the fact.

The **threshold** that decides auto-apply vs. approval is **not** a constant: it lives in
`InventorySettings` (one row per company: `VarianceThreshold` + a `VarianceThresholdType` discriminator so
"absolute qty" vs "percentage of system qty" stays open — the units are unspecified upstream). Each
`StockCount` **snapshots** the threshold value+type in force at count time, so a later config change never
retroactively re-adjudicates an already-decided count. Over-threshold ⇒ `Status = PendingApproval` (BR-014);
on approval the reconciling movement is written to the ledger and linked via `AdjustmentTransactionId`.

### 2.5 BR-003 tenant isolation on scan resolve is a first-class outcome, not an omission
`ScanResolvedType.Blocked` is distinct from `NoMatch`. When a raw code matches a real entity outside the
scanner's reachable tenant/company set, the resolver records `Blocked` **and writes no
`ResolvedEntityId`** — the other tenant's id is never persisted, so the scan log itself cannot leak it.
`NoMatch` (matched nothing) is kept separate so "belongs to someone else" is never conflated with "doesn't
exist." Reachability reuses the exact Phase-1 rule (home company + enabled `CompanyConnection`s), so scanning
inherits BR-TC-001/005 for free.

### 2.6 `ScanEvent.ResolvedEntityId` is deliberately FK-less (polymorphic + forward-compat with F5)
The resolved target is polymorphic — an `InventoryItem`, a `Location`/`Bin`, or (from F5) a Fixed Asset whose
table doesn't exist yet. A single hard FK can't span the alternatives, and the Asset arm has nothing to point
at today. `ResolvedType` + `ResolvedEntityId` is a routing tag pair on a **log** row (not a relational parent
of those entities), so the missing FK is by design — and F5 adding the Assets table requires **no** change
here.

### 2.7 Denormalized ancestry on stock/count rows — same hot-path rationale as Phase 1
`StockLevels` and `StockCounts` denormalize the bin's full ancestor chain
(`RackId`/`WarehouseId`/`LocationId`/`CompanyId`/`TenantId`). This lets the cross-tenant effective-access
check (README §3) and company-scoped reads run as direct indexed comparisons against the stock row, with no
join up to `Bins`. `InventoryTransactions` denormalizes `CompanyId`/`TenantId` only (its two bin FKs each
already carry their own ancestry).

### 2.8 Inventory master + stock participate in the existing offline sync
`InventoryItems`, `StockLevels`, `InventoryTransactions`, and `StockCounts` each carry the shared
`SyncCursorUtc` shadow column + a company-scoped `(CompanyId, SyncCursorUtc, Id)` keyset index
(`IX_<Table>_Sync`), so the mobile client pulls and reads them offline through the **same** cursor mechanism
documented in `docs/api/master-data-sync.md` — no new pagination contract. `ScanEvents` and
`InventorySettings` are intentionally **not** cursor-synced (scan logs are server-side audit; settings are
low-cardinality config fetched directly).

### 2.9 Conventions inherited verbatim from Phase 1
GUID PKs; enums as `varchar(20)` + `AddEnumCheck<T>()` CHECK; `now()` default on `CreatedAtUtc`; all
`DateTime` → `timestamptz` (context `ConfigureConventions`); `Restrict`/`NoAction` on cross-aggregate FKs +
soft-delete via `IsActive` on master data; decimals pinned to `numeric(18,4)` via `HasPrecision(18, 4)`.

---

## 3. Tables, columns, keys

### `InventoryItems` (SKU master, tenant-scoped, sync-enabled)
`Id` (uuid PK) · `TenantId` · `CompanyId`→Companies (Restrict) · `Sku` (varchar 100) · `Barcode?` (varchar 100) ·
`Name` (varchar 200) · `Description?` (varchar 1000) · `UnitOfMeasure?` (varchar 20) · `Category?` (varchar 200) ·
`IsActive` · `CreatedAtUtc` (now()) · `UpdatedAtUtc?` · `SyncCursorUtc` (generated) · `xmin`.
**Indexes:** UQ `(CompanyId, Sku)` · `(CompanyId, Barcode)` filtered NOT NULL · `TenantId` ·
`IX_InventoryItems_Sync (CompanyId, SyncCursorUtc, Id)`.

### `StockLevels` (per-bin on-hand, the mutable source of truth)
`Id` (uuid PK) · `InventoryItemId`→InventoryItems (Restrict) · `BinId`→Bins (Restrict) ·
ancestry `RackId`/`WarehouseId`/`LocationId`/`CompanyId`/`TenantId` · `QuantityOnHand` (numeric 18,4) ·
`Version` (bigint) · `CreatedAtUtc` (now()) · `UpdatedAtUtc?` · `SyncCursorUtc` (generated) · `xmin`.
**Constraints:** `CK_StockLevels_NonNegative (QuantityOnHand >= 0)`.
**Indexes:** UQ `(InventoryItemId, BinId)` · `(BinId)` · `(CompanyId, InventoryItemId)` · `TenantId` ·
`IX_StockLevels_Sync`.

### `InventoryTransactions` (append-only ledger)
`Id` (uuid PK) · `TenantId` · `CompanyId` · `InventoryItemId`→InventoryItems (Restrict) ·
`TransactionType` (Receive/Transfer/Adjustment) · `SourceBinId?`→Bins (Restrict) ·
`DestinationBinId?`→Bins (Restrict) · `Quantity` (numeric 18,4) · `AdjustmentReason?` (varchar 400) ·
`Status` (Applied/Rejected) · `RejectionReason?` · `IdempotencyKey` (varchar 200) ·
`BaseSourceStockVersion?` · `BaseDestinationStockVersion?` · `ReversalOfTransactionId?`→self (Restrict) ·
`DeviceId?` · `CreatedByUserId`→Users (Restrict) · `ClientCreatedAtUtc?` · `CreatedAtUtc` (now()) ·
`UpdatedAtUtc?` · `SyncCursorUtc` (generated).
**Constraints:** `CK_InvTxn_TypeShape` (§2.3) · `CK_InvTxn_AdjustmentReason`.
**Indexes:** UQ `(IdempotencyKey)` · `(CompanyId, CreatedAtUtc)` · `(InventoryItemId)` ·
`(SourceBinId)` filtered · `(DestinationBinId)` filtered · `TenantId` · `IX_InventoryTransactions_Sync`.

### `StockCounts` (count vs variance, approval workflow)
`Id` (uuid PK) · `TenantId` · `CompanyId` · `InventoryItemId`→InventoryItems (Restrict) · `BinId`→Bins (Restrict) ·
ancestry `RackId`/`WarehouseId`/`LocationId` · `CountedQuantity` (numeric 18,4) · `SystemQuantity` (numeric 18,4) ·
`Variance` (numeric 18,4, generated) · `VarianceThreshold` (numeric 18,4, snapshot) · `VarianceThresholdType` (snapshot) ·
`Status` (Completed/PendingApproval/Approved/Rejected) · `CountedByUserId`→Users (Restrict) ·
`ApprovedByUserId?`→Users (Restrict) · `ApprovedAtUtc?` · `RejectionReason?` · `AdjustmentTransactionId?`→InventoryTransactions (Restrict) ·
`IdempotencyKey` (varchar 200) · `BaseStockVersion?` · `DeviceId?` · `ClientCreatedAtUtc?` ·
`CreatedAtUtc` (now()) · `UpdatedAtUtc?` · `SyncCursorUtc` (generated) · `xmin`.
**Constraints:** `CK_StockCounts_CountedNonNegative`.
**Indexes:** UQ `(IdempotencyKey)` · `(CompanyId, Status)` filtered `Status='PendingApproval'` (approval queue) ·
`(BinId)` · `(InventoryItemId)` · `TenantId` · `IX_StockCounts_Sync`.

### `InventorySettings` (per-company configurable threshold)
`Id` (uuid PK) · `TenantId` · `CompanyId`→Companies (Restrict) · `VarianceThreshold` (numeric 18,4) ·
`VarianceThresholdType` (default AbsoluteQuantity) · `CreatedAtUtc` (now()) · `UpdatedAtUtc?` · `xmin`.
**Indexes:** UQ `(CompanyId)`.

### `ScanEvents` (F3 audit log)
`Id` (uuid PK) · `TenantId` · `CompanyId` · `ScannedByUserId`→Users (Restrict) · `RawCode` (varchar 400) ·
`ResolvedType` (Sku/Location/Asset/NoMatch/Blocked) · `ResolvedEntityId?` (uuid, **no FK**, §2.6) ·
`DeviceId?` · `ScannedAtUtc` · `IdempotencyKey?` (varchar 200) · `CreatedAtUtc` (now()).
**Indexes:** `(CompanyId, ScannedAtUtc)` · `(ScannedByUserId, ScannedAtUtc)` · `(CompanyId, RawCode)` ·
UQ `(IdempotencyKey)` filtered NOT NULL · `TenantId`.

---

## 4. Edge cases the schema supports cleanly

| Edge case | How the schema handles it |
|---|---|
| Transfer drives source negative / over-source | `CK_StockLevels_NonNegative` rejects a resulting negative on-hand at the DB; handler pre-checks for a clean 4xx. |
| Invalid destination bin | `DestinationBinId` FK to `Bins` (Restrict); `Transfer` src==dst blocked by `CK_InvTxn_TypeShape`. |
| Variance over threshold ⇒ approval | `StockCounts.Status = PendingApproval` + `(CompanyId, Status)` filtered approval-queue index; threshold snapshotted from `InventorySettings`, not hardcoded. |
| Offline commit then server-side conflict | `Base*StockVersion` stamps vs `StockLevel.Version` (semantic) + `xmin` (DB interlock) ⇒ backend detects and returns 409. |
| Replayed offline push (double delivery) | Unique `IdempotencyKey` on `InventoryTransactions`/`StockCounts`/`ScanEvents` ⇒ de-duplicated no-op. |
| Cross-tenant scanned code (BR-003) | `ScanResolvedType.Blocked` with NULL `ResolvedEntityId` ⇒ no leak; distinct from `NoMatch`. |
| Correcting a bad movement | New offsetting `Adjustment` with `ReversalOfTransactionId`; ledger stays append-only. |

---

## 5. DbContext wiring the backend engineer must add (Npgsql-only)

Add the DbSets and extend the existing `if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")`
block in `IamsDbContext.OnModelCreating`. All three concerns below already have a precedent in that block
(sync cursor, `xmin`) — this just extends them to the new entities. **They are Npgsql-only** because the
in-memory test provider can evaluate neither a generated column nor `xmin`; there the `SyncCursorUtc` /
`Variance` stay plain writable properties tests can seed.

```csharp
// DbSets
public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
public DbSet<StockLevel> StockLevels => Set<StockLevel>();
public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
public DbSet<StockCount> StockCounts => Set<StockCount>();
public DbSet<InventorySettings> InventorySettings => Set<InventorySettings>();
public DbSet<ScanEvent> ScanEvents => Set<ScanEvent>();

// inside the Npgsql-only block in OnModelCreating:

// (a) xmin optimistic-concurrency tokens (same pattern as User/CompanyConnection)
foreach (var clr in new[] { typeof(StockLevel), typeof(StockCount),
                            typeof(InventoryItem), typeof(InventorySettings) })
{
    modelBuilder.Entity(clr).Property<uint>("xmin")
        .HasColumnName("xmin").ValueGeneratedOnAddOrUpdate().IsRowVersion();
}

// (b) master-data sync cursor: STORED generated COALESCE(UpdatedAtUtc, CreatedAtUtc)
const string cursorSql = "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")";
foreach (var clr in new[] { typeof(InventoryItem), typeof(StockLevel),
                            typeof(InventoryTransaction), typeof(StockCount) })
{
    modelBuilder.Entity(clr).Property<DateTime>(MasterData.SyncCursor.ColumnName)
        .HasComputedColumnSql(cursorSql, stored: true);
}

// (c) StockCount.Variance: STORED generated CountedQuantity - SystemQuantity
modelBuilder.Entity<StockCount>().Property(x => x.Variance)
    .HasComputedColumnSql("\"CountedQuantity\" - \"SystemQuantity\"", stored: true);
```

> **`StockLevel.Version` is application-managed** (like `PolicyRevision`): the backend MUST bump it in the
> same transaction as each applied movement. Consider doing it centrally in `SaveChanges` (mirroring
> `BumpPolicyRevisions`) so no mutation path can forget — that omission would silently break offline conflict
> detection. This is the single most important integrity obligation this schema hands to the service layer.

---

## 6. Migration safety & open gaps

- **Greenfield-additive:** all new `CREATE TABLE`s referencing existing hierarchy/`Users`/`Companies`. No
  backfill, no column-tightening on populated tables (contrast the Phase-1 Activation-Key fix in README §7),
  so no "existing row violates a new NOT NULL/UNIQUE/CHECK" hazard. `Down` drops the six new tables only.
- **`InventorySettings` bootstrap:** a company with no settings row has no configured threshold. Backend
  must decide the fallback (recommended: treat missing as a documented default threshold, or seed a row per
  company) — flagged, not decided here, since the default value is source-unspecified.
- **Open gaps deliberately left open:** threshold *units* (`VarianceThresholdType` keeps absolute vs
  percentage open); whether stock-count approval is the same dual-control mechanism as F7 (schema is
  compatible — `ApprovedByUserId`/`ApprovedAtUtc` are generic); whether `ScanEvents` should ever be pruned
  (currently unbounded audit log — a retention policy can be added later without touching the shape).

---

## 7. Verification status

**Design-verified, integration-pending.** The reference EF code is written against the live `src` conventions
(cross-checked against `HierarchyConfigurations.cs`, `ConnectionAndAuthConfigurations.cs`, `IamsDbContext.cs`,
`SyncCursor.cs`) but is **not compiled or migrated in this pass** — it sits in `reference-model/`, outside the
compiled `src` assembly, and file ownership of `src` is the `dotnet-backend-engineer`'s. Handoff checklist for
that engineer (mirrors Phase 1 §7):

1. Copy the four reference files to the `src` paths in §1; add the DbSets + Npgsql wiring from §5.
2. `dotnet build` (expect 0 warnings).
3. `dotnet ef migrations add AddInventoryAndScanning` → review the generated `Up`/`Down`; confirm the six
   tables, the CHECKs (`CK_InvTxn_TypeShape`, `CK_InvTxn_AdjustmentReason`, `CK_StockLevels_NonNegative`,
   `CK_StockCounts_CountedNonNegative`, the enum CHECKs), the two STORED generated columns, and the `xmin`
   columns all appear.
4. `dotnet ef migrations has-pending-model-changes` → clean.
5. `dotnet ef database update` against a real Postgres; spot-check with `psql` that `Variance` and
   `SyncCursorUtc` are `GENERATED ALWAYS … STORED` and the CHECKs exist.
6. Add opt-in SQL integration tests (gated by `IAMS_PG_TEST_CONN`) proving, on real Postgres: the non-negative
   CHECK blocks an over-source transfer; the per-type CHECK blocks a malformed row; `IdempotencyKey`
   de-dupes a replayed insert; a stale `Base*StockVersion` is detectable; and the `Variance` generated column
   computes correctly.
```
