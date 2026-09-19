# API Contract — Phase 2a: F3 Scanning + F4 Inventory Operations

Owner: `dotnet-backend-engineer`. This is the authoritative contract the Flutter mobile client implements
against for the Scanner (F3) and Inventory Operations (F4) screens. Do not invent alternative shapes — if
something is missing or wrong, raise it with the Team Lead and this doc is updated first.

Conventions (unchanged from `master-data-sync.md` and still in force): base path `/api`; enums serialized as
strings; `bool`/`Guid`/`decimal`/ISO-8601-with-offset timestamps via System.Text.Json camelCase; ProblemDetails
error shape with a stable machine-readable `code` extension. Every endpoint here `RequireAuthorization()` — a
valid access token is required; a missing/expired token is the standard `401`. **Tenant scope is always
resolved live off the caller's JWT (`CompanyId` claim) + reachable-company set** (home company + enabled
`CompanyConnection`s, exactly as master-data sync). A client-supplied tenant/company is never trusted or
accepted.

---

## What's new vs. Phase 1 / master-data-sync

- **Offline-safe mutations.** Every write accepts a client-generated **`idempotencyKey`** so a replayed
  offline push is de-duplicated, and optional **base stock version(s)** so a mutation built on stale offline
  state is rejected with a `409` instead of silently applying. These two mechanisms are independent — see
  [Idempotency & conflict semantics](#idempotency--conflict-semantics).
- **New error codes:** `stock_version_conflict` (409), `insufficient_stock` (422), `stock_count_not_pending`
  (409). Existing `validation_failed` (400) and `not_found` (404) are reused.

---

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/scan/resolve` | F3 — resolve a raw scanned/typed code to an entity |
| `GET`  | `/api/inventory/items` | F4 — search/filter inventory list |
| `GET`  | `/api/inventory/items/{id}` | F4 — item detail: qty by bin + movement history |
| `POST` | `/api/inventory/receive` | F4 — receive stock into a bin |
| `POST` | `/api/inventory/transfer` | F4 — move stock between two bins |
| `POST` | `/api/inventory/adjust` | F4 — signed manual adjustment |
| `POST` | `/api/inventory/counts` | F4 — record a stock count (auto-apply or park for approval) |
| `POST` | `/api/inventory/counts/{id}/approve` | F4 — approve a pending count |
| `POST` | `/api/inventory/counts/{id}/reject` | F4 — reject a pending count |

---

## F3 — `POST /api/scan/resolve`

Resolves one raw code and writes a `ScanEvent` audit row for every attempt (including no-match/blocked).

**Request**
```jsonc
{
  "rawCode": "SKU-1",          // required, <= 400 chars
  "deviceId": "abc123",        // optional, <= 200
  "scannedAtUtc": "2026-09-16T10:00:00+00:00", // optional; server time used if absent
  "idempotencyKey": "scan-uuid" // optional, <= 200; replaying it returns the original outcome
}
```

**Response `200`**
```jsonc
{
  "resolvedType": "Sku",       // Sku | Location | Asset | NoMatch | Blocked
  "resolvedEntityId": "guid",  // null for NoMatch and Blocked
  "label": "Widget",           // item name / bin label when known; else null
  "scanEventId": "guid"
}
```

**Resolution order & semantics**

1. **`Sku`** — the code equals an active `InventoryItem.Sku` **or** `Barcode` within the reachable set →
   `resolvedEntityId` = item id.
2. **`Location`** — the code equals an active `Bin.Name` (its physical label) within the reachable set →
   `resolvedEntityId` = bin id.
3. **`Blocked`** (BR-003) — the code matches a real SKU/barcode/bin **outside** the reachable set.
   `resolvedEntityId` is **`null`** and the audit row stores no id — a cross-tenant entity's existence/id is
   never leaked. Deliberately distinct from `NoMatch`.
4. **`NoMatch`** — matched nothing anywhere.
5. **`Asset`** — **reserved for F5.** There is no Fixed Assets table yet, so the current resolver **never
   emits `Asset`**; the value exists in the contract so the client can handle it without a breaking change
   when F5 lands. **Clients must tolerate it now** (treat as "navigate to asset detail" once F5 exists).

> Decision flagged to Lead: "Location" resolves against **`Bin.Name`** (the leaf scannable label), since the
> hierarchy has no dedicated code column. Revisit if a dedicated location-barcode field is added.

---

## F4 — `GET /api/inventory/items`

Online, reachable-scoped inventory listing for the F4 list screen. **Offset-paginated** (distinct from the
offline sync feed, which rides the shared `SyncCursor` mechanism on `InventoryItems`/`StockLevels` — now
shipped as `GET /api/inventory/sync/items` + `GET /api/inventory/sync/stock-levels`, documented in
`master-data-sync.md` → "Inventory Offline Sync").

**Query params** (all optional)

| Param | Type | Default | Rules |
|---|---|---|---|
| `search` | string | absent | Case-insensitive substring over SKU / name / barcode. |
| `filter` | string | `all` | `all` \| `in_stock` \| `low_stock` \| `out_of_stock`. Invalid → `400`. |
| `lowStockThreshold` | decimal | `10` | Inclusive upper bound for `low_stock` (`0 < total <= threshold`). `>= 0`. |
| `page` | int | `1` | 1-based. `> 0`. |
| `pageSize` | int | `50` | `> 0` and `<= 200`. Out of range → `400`. |

Filters act on **total on-hand across all of the item's bins**: `in_stock` = `total > 0`, `out_of_stock` =
`total <= 0`, `low_stock` = `0 < total <= lowStockThreshold`.

> Decision flagged to Lead: there is no per-SKU reorder point in the Phase-2a schema, so `low_stock` uses a
> single request-overridable threshold (default `10`), not a per-item level.

**Response `200`**
```jsonc
{
  "items": [
    { "id": "guid", "sku": "SKU-1", "barcode": "BC-1", "name": "Widget",
      "unitOfMeasure": "EA", "category": "Tools", "isActive": true, "totalQuantityOnHand": 12.0000 }
  ],
  "page": 1,
  "pageSize": 50,
  "hasMore": false   // peek-ahead (pageSize+1); no separate COUNT
}
```

## F4 — `GET /api/inventory/items/{id}`

**Response `200`**
```jsonc
{
  "id": "guid", "tenantId": "guid", "companyId": "guid",
  "sku": "SKU-1", "barcode": "BC-1", "name": "Widget", "description": null,
  "unitOfMeasure": "EA", "category": "Tools", "isActive": true,
  "totalQuantityOnHand": 12.0000,
  "stockByBin": [
    { "binId": "guid", "rackId": "guid", "warehouseId": "guid", "locationId": "guid",
      "companyId": "guid", "quantityOnHand": 12.0000, "version": 3 }  // version = stamp for offline mutations
  ],
  "movements": [   // most-recent first, capped at 50
    { "id": "guid", "transactionType": "Receive", "status": "Applied",
      "sourceBinId": null, "destinationBinId": "guid", "quantity": 10.0000,
      "adjustmentReason": null, "createdAtUtc": "...", "clientCreatedAtUtc": null }
  ]
}
```

- **`404 not_found`** when the id is outside the caller's reachable set — deliberately **not `403`**, so a
  direct object reference cannot confirm another tenant's item exists (BR-003 spirit). This differs from the
  master-data parent-ownership `403`/`404` split, on purpose, for a direct-by-id lookup.

---

## F4 mutations — receive / transfer / adjust

All three return the same **`StockMovementResponse`** and share idempotency + version semantics.

### `POST /api/inventory/receive`
```jsonc
{ "idempotencyKey": "k", "inventoryItemId": "guid", "destinationBinId": "guid",
  "quantity": 10.0,                        // > 0
  "baseDestinationStockVersion": 2,        // optional; conflict-check the destination bin
  "deviceId": "abc", "clientCreatedAtUtc": "..." }  // both optional
```

### `POST /api/inventory/transfer`
```jsonc
{ "idempotencyKey": "k", "inventoryItemId": "guid",
  "sourceBinId": "guid", "destinationBinId": "guid",  // must differ
  "quantity": 4.0,                                     // > 0, <= source on-hand
  "baseSourceStockVersion": 3, "baseDestinationStockVersion": 1, // optional
  "deviceId": "abc", "clientCreatedAtUtc": "..." }
```

### `POST /api/inventory/adjust`
```jsonc
{ "idempotencyKey": "k", "inventoryItemId": "guid", "binId": "guid",
  "quantityDelta": -3.0,        // signed, non-zero; result must stay >= 0
  "reason": "Damaged",          // required, <= 400
  "baseStockVersion": 5,        // optional; conflict-check the bin
  "deviceId": "abc", "clientCreatedAtUtc": "..." }
```

**Response `200` — `StockMovementResponse`**
```jsonc
{
  "transactionId": "guid",
  "transactionType": "Transfer",   // Receive | Transfer | Adjustment
  "status": "Applied",
  "quantity": 4.0000,
  "adjustmentReason": null,
  "stockLevels": [                 // resulting state of each affected bin
    { "binId": "guid", "quantityOnHand": 6.0000, "version": 4 },
    { "binId": "guid", "quantityOnHand": 4.0000, "version": 2 }
  ],
  "replayed": false                // true when this was an idempotent replay (no new effect)
}
```

---

## F4 stock count — `POST /api/inventory/counts`

Records a physical count of one item in one bin and adjudicates it against the company's variance threshold in
one transaction.

```jsonc
{ "idempotencyKey": "k", "inventoryItemId": "guid", "binId": "guid",
  "countedQuantity": 12.0,   // >= 0
  "baseStockVersion": 3,     // optional; conflict-check the bin
  "deviceId": "abc", "clientCreatedAtUtc": "..." }
```

**Response `200` — `StockCountResponse`** (also returned by approve/reject)
```jsonc
{
  "id": "guid",
  "status": "Completed",          // Completed | PendingApproval | Approved | Rejected
  "countedQuantity": 12.0000,
  "systemQuantity": 10.0000,      // on-hand snapshot at count time
  "variance": 2.0000,             // counted - system (server-computed)
  "varianceThreshold": 5.0000,    // snapshotted from InventorySettings (or fallback)
  "varianceThresholdType": "AbsoluteQuantity",  // AbsoluteQuantity | Percentage
  "adjustmentTransactionId": "guid", // the reconciling ledger adjustment, when one was written; else null
  "stockVersion": 4,
  "replayed": false
}
```

**Adjudication (BR-014)**
- **Within threshold** → `status: "Completed"`, applied immediately: stock is set to `countedQuantity` and,
  if it differs from `systemQuantity`, a reconciling `Adjustment` is written to the ledger and linked via
  `adjustmentTransactionId`.
- **Over threshold** → `status: "PendingApproval"`, **no stock change** until approval.
- Threshold interpretation: `AbsoluteQuantity` → `|variance| <= threshold`; `Percentage` →
  `|variance| / systemQuantity * 100 <= threshold` (a zero system qty only passes on an exact match).

> **Decision flagged to Lead — `InventorySettings` bootstrap.** A company with **no** settings row uses a
> documented fallback of **threshold `0`, type `AbsoluteQuantity`** ⇒ *any* non-zero variance goes to
> `PendingApproval`. This is the fail-safe direction (never silently auto-adjust for an unconfigured company);
> an admin loosens it by inserting an `InventorySettings` row. The fallback value is source-unspecified — this
> is our choice, recorded here.

### `POST /api/inventory/counts/{id}/approve`
No body. Brings the bin's on-hand to the counted quantity (delta computed against **current** on-hand at
approval time), writes the reconciling `Adjustment`, records the approver, and returns `StockCountResponse`
with `status: "Approved"`. A count not in `PendingApproval` → `409 stock_count_not_pending`.

### `POST /api/inventory/counts/{id}/reject`
```jsonc
{ "reason": "miscount" }   // optional, <= 400; body may be omitted entirely
```
Terminal `status: "Rejected"`, no stock change. Non-pending → `409 stock_count_not_pending`.

---

## Idempotency & conflict semantics

These are **two independent guards** — a queued offline mutation carries both.

### 1. Idempotency (`idempotencyKey`) — "don't double-apply the *same* mutation"
- **Required** on receive/transfer/adjust/count (`<= 200` chars); **optional** on scan-resolve.
- Replaying a key that already committed is a **no-op that returns the original result** with
  **`"replayed": true`** and HTTP **`200`** — never an error, never a second effect.
- **First-write-wins.** A key reused with a *different* payload still returns the **original** stored
  transaction/count; the second payload is ignored. Treat the key as a stable id for one logical mutation.
- Backed by a unique DB index; a concurrent double-delivery race is resolved server-side to the same replay
  outcome (verified on real Postgres).

### 2. Concurrency (`base*StockVersion`) — "someone else changed the bin first"
- Each `StockLevel` has a monotonic **`version`** (readable from item-detail `stockByBin[].version` and echoed
  in every mutation response). It is bumped **in the same transaction** as every applied on-hand change.
- Stamp the version(s) you observed at offline-commit time on the mutation (`baseSourceStockVersion` /
  `baseDestinationStockVersion` / `baseStockVersion`). If a stamped value **≠** the server's current version at
  push, the mutation is rejected with **`409 stock_version_conflict`** and **not applied**.
- **Omit** the base version(s) to skip the check (last-writer-wins) — e.g. an online action with no stale
  offline basis. A not-yet-existing stock row is version `0`.

**`409 stock_version_conflict` body** carries the current state per affected bin so the client can rebase
without another round-trip:
```jsonc
{
  "title": "...", "status": 409, "code": "stock_version_conflict",
  "conflicts": [
    { "binId": "guid", "expectedVersion": 0, "currentVersion": 3, "currentQuantityOnHand": 12.0000 }
  ]
}
```

**`422 insufficient_stock` body** (over-source transfer / over-decrement adjustment):
```jsonc
{ "title": "...", "status": 422, "code": "insufficient_stock",
  "binId": "guid", "availableQuantity": 3.0000 }
```

---

## Error codes

| `code` | Status | Meaning / client action |
|---|---|---|
| `validation_failed` | 400 | Shape error: missing field, non-positive quantity, zero adjustment delta, missing adjustment reason, `sourceBinId == destinationBinId`, bad `filter`/`pageSize`/`page`. |
| `not_found` | 404 | Item / bin / stock count not found **or outside the reachable set** (no existence leak). |
| `stock_version_conflict` | 409 | Stamped `base*StockVersion` ≠ current; body lists `conflicts[]`. Re-read and retry. |
| `stock_count_not_pending` | 409 | Approve/reject on a count that isn't `PendingApproval`. |
| `insufficient_stock` | 422 | Movement would drive a bin below zero (caught before the DB CHECK). |
| (none) | 401 | Missing/expired access token (standard ASP.NET Core auth failure, no `code` body). |

---

## Notes for testing

Handler-level behavior (resolution + BR-003 blocking, idempotent replay, the 409 version conflict, the 422
non-negative pre-check, variance/threshold branching, reachable-company scoping) is covered in
`InventoryScanningHandlerTests` (in-memory). The provider-specific guarantees — the `CK_StockLevels_NonNegative`
and per-type `CK_InvTxn_TypeShape` CHECKs, the unique `IdempotencyKey` de-dupe, the `Variance` STORED generated
column, and end-to-end stale-version detection — are verified against real PostgreSQL in
`InventorySqlIntegrationTests` (opt-in via `IAMS_PG_TEST_CONN`).
