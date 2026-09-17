# API Contract — Master-Data Offline Sync (Company → Location → Warehouse → Rack → Bin)

Owner: `dotnet-backend-engineer`. This is the authoritative contract the Flutter mobile client
implements against for syncing the physical hierarchy into its local SQLite store. Do not invent
alternative shapes — if something is missing or wrong, raise it with the Team Lead and this doc is
updated first.

Conventions (unchanged from the other API docs and still in force here): base path `/api`; enums
serialized as strings; `bool`/`Guid`/ISO-8601-with-offset timestamps via System.Text.Json camelCase;
ProblemDetails error shape with a stable machine-readable `code` extension. All five endpoints
`RequireAuthorization()` — a valid access token (from `POST /api/auth/activate` →
`POST /api/auth/refresh`) is required; a missing/expired token is the standard `401`.

## What's new

Five read-only, paginated listing endpoints — one per hierarchy level — that expose the master-data
rows a caller is entitled to, so the mobile client can pull them once and then read them fully
offline. Before this, no endpoint returned entity listings (`/api/me/scope` and
`/api/access/evaluate(-batch)` return access decisions / thin refs only).

```
GET /api/master-data/companies?cursor=&pageSize=
GET /api/master-data/locations?parentId=&cursor=&pageSize=
GET /api/master-data/warehouses?parentId=&cursor=&pageSize=
GET /api/master-data/racks?parentId=&cursor=&pageSize=
GET /api/master-data/bins?parentId=&cursor=&pageSize=
```

### Query parameters (all optional)

| Param | Type | Default | Rules |
|---|---|---|---|
| `cursor` | string | absent | Opaque keyset cursor from a previous response's `nextCursor`. Absent/empty = start of the world (initial/full sync). A non-empty but malformed cursor → `400 validation_failed`. |
| `pageSize` | int | `200` | `> 0` and `<= 500` (same cap precedent as `/api/access/evaluate-batch`). Out of range → `400 validation_failed`. |
| `parentId` | GUID | absent | **Companies has no `parentId`.** For the other four levels, when supplied it scopes the page to a single parent and is validated for ownership (see below). When supplied it must be non-empty. |

`parentId` is the **immediate parent's** id, one level up:

| Endpoint | `parentId` refers to a… |
|---|---|
| `locations` | Company |
| `warehouses` | Location |
| `racks` | Warehouse |
| `bins` | Rack |

## Scope: "reachable companies"

Every level is filtered to the caller's **reachable company set** = the caller's own home company
(`CompanyId` claim) **plus every company reachable via an _enabled_ `CompanyConnection`** from it.
This is filtered on the denormalized `companyId` present on every level (no per-parent iteration).
Disabled connections grant nothing and are excluded (BR-TC-001/005) — note this differs from
`GET /api/me/scope`, which deliberately surfaces disabled connections too, for UI display.

## Response envelope

```json
{
  "items": [ /* level DTOs, ordered by (syncCursor, id) ascending */ ],
  "nextCursor": "b64url-opaque-string-or-null",
  "hasMore": true
}
```

### `items` — per-level DTOs

Each DTO carries every denormalized ancestor id already on the row (free columns — the client needs
no joins), plus the common fields. Field names/types are exactly:

```jsonc
// GET /api/master-data/companies
{ "id": "guid", "name": "Acme", "isActive": true,
  "createdAtUtc": "2026-01-01T00:00:00+00:00", "updatedAtUtc": "2026-02-01T00:00:00+00:00", // updatedAtUtc nullable
  "tenantId": "guid" }

// GET /api/master-data/locations
{ "id": "guid", "name": "L", "isActive": true,
  "createdAtUtc": "...", "updatedAtUtc": null,
  "tenantId": "guid", "companyId": "guid", "region": "APAC" }        // region nullable

// GET /api/master-data/warehouses
{ "id": "guid", "name": "W", "isActive": true,
  "createdAtUtc": "...", "updatedAtUtc": null,
  "tenantId": "guid", "locationId": "guid", "companyId": "guid" }

// GET /api/master-data/racks
{ "id": "guid", "name": "R", "isActive": true,
  "createdAtUtc": "...", "updatedAtUtc": null,
  "tenantId": "guid", "warehouseId": "guid", "locationId": "guid", "companyId": "guid" }

// GET /api/master-data/bins
{ "id": "guid", "name": "B", "isActive": true,
  "createdAtUtc": "...", "updatedAtUtc": null,
  "tenantId": "guid", "rackId": "guid", "warehouseId": "guid", "locationId": "guid", "companyId": "guid" }
```

## Pagination & incremental sync — one cursor mechanism

The `cursor` is an **opaque keyset cursor**: base64url of `"{syncCursorTicksUtc:D19}|{id}"`, where
`syncCursorUtc = COALESCE(updatedAtUtc, createdAtUtc)`. Rows are ordered by `(syncCursorUtc, id)`
ascending, tie-broken by `id`. The same cursor serves both bulk pagination and incremental "since"
sync — there is no separate `since`/`page` param. Treat it as fully opaque; do not parse it.

- **Peek-ahead pagination.** The server fetches `pageSize + 1` rows to compute `hasMore` without a
  separate COUNT. `hasMore == true` means more rows remain beyond this page; keep calling with
  `nextCursor` until `hasMore == false`.
- **`nextCursor` is the tail of the returned page.** Pass it back verbatim as `cursor` to continue.
- **⚠️ Empty page ⇒ `nextCursor: null` means "nothing new," NOT "reset".** When a page comes back
  with `items: []`, `nextCursor` is `null` and `hasMore` is `false`. This is the normal "you are
  caught up" response for an incremental poll. **The client must NEVER clear/overwrite its stored
  cursor on an empty page** — doing so would silently force a full re-pull next time. Persist a new
  cursor only from a **non-empty** page.
- A non-empty final page returns a **non-null** `nextCursor` even though `hasMore` is `false` — that
  cursor is the client's "resume from here next sync" position.

## Soft deletes

Rows soft-deleted server-side (`isActive: false`) are delivered in a normal page exactly like any
other changed row — **they are never excluded**. The client should **upsert** every row it receives
and mark `isActive=false` locally; it must **never hard-delete** a local row just because a page
didn't mention it (an incremental page only contains rows changed since the cursor). A row appears
in a page whenever its `syncCursorUtc` advances, including when it's soft-deleted.

## Parent-ownership errors (`parentId`)

When `parentId` is supplied on `locations`/`warehouses`/`racks`/`bins`, the server resolves the
parent row's owning company via one indexed lookup and enforces:

| Situation | Status | `code` |
|---|---|---|
| Parent row does not exist | `404` | `not_found` |
| Parent exists but its company is **not** in the caller's reachable set | `403` | `access_denied` |

The split is deliberate: `404` = "no such parent at all," `403` = "it exists but isn't yours." After
the ownership check passes, the page is additionally filtered by exact `parentId` equality (defense
in depth on top of the reachable-company filter).

## Errors

| `code` | Status | Meaning / client action |
|---|---|---|
| `validation_failed` | 400 | `pageSize` out of `(0, 500]`, malformed `cursor`, or empty `parentId`. |
| `not_found` | 404 | Supplied `parentId` doesn't resolve to an existing parent row. |
| `access_denied` | 403 | Supplied `parentId` exists but its company isn't reachable. |
| (none) | 401 | Missing/expired access token (standard ASP.NET Core auth failure, no `code` body). |

## ⚠️ Known gap for the mobile team — newly-reachable companies

Incremental sync keys off row timestamps (`COALESCE(updatedAtUtc, createdAtUtc)`). When a company
becomes newly reachable because an admin **enables a `CompanyConnection`**, **nothing about the
company row itself changes** — its `syncCursorUtc` does not advance. So an incremental "since" fetch
on `companies` alone will **not** surface a newly-reachable company (nor will its children surface
by timestamp, since their rows didn't change either).

**Mobile-side mitigation (mobile's responsibility, flagged here so it's not a surprise):** on every
sync pass, fetch the `companies` level **in full** (`cursor` absent — it's cheap and
low-cardinality), diff the returned id set against a locally persisted snapshot of previously-known
company ids, and on any change force a full re-pull (`cursor=null`) of `locations`→`bins`. The
reverse — a connection being _disabled_ — simply drops those companies out of the reachable set, so
they stop appearing from the API. **Pruning is not yet implemented client-side**: the mobile sync
engine detects the drop via the same full-Company id-set diff, but currently only uses that diff to
force a re-pull of `locations`→`bins` for companies that *appear*; it does not delete local rows for
companies (or their descendants) that *disappear* from the reachable set. Those rows persist in
SQLite indefinitely after the server-side connection is disabled — a real data-retention gap to
close before any screen displays this data, tracked as a follow-up rather than blocking this round.

## ⚠️ Known gap for the mobile team — steady-state incremental sync of already-`complete` levels

Once a child level (`locations`/`warehouses`/`racks`/`bins`) reaches `complete` for the current
reachable-company set, `HierarchySyncService.run()` skips it on every subsequent pass — the
per-level `last_cursor` is currently only used to *resume an interrupted initial sync*, never as
the starting point for an incremental "since" poll once complete. Practical effect: after the
initial full sync, edits to already-reachable companies' descendants (a new location, a renamed
warehouse, a soft-deleted bin) never reach the device. Only a **newly-reachable company**
(reachability diff, see above) forces a re-pull, and that path does a full re-pull rather than an
incremental one anyway. This matches the approved design's literal "skip if already complete"
wording, so it's a design-scope gap rather than an implementation bug — flagged here (round-2 code
review, 2026-09-16) since no UI consumes `HierarchyRepository` yet, so there's no user-visible
impact today. Close before any screen relies on this data staying fresh post-initial-sync: either
widen `run()` to always attempt an incremental cursor-forward fetch on complete levels (cheap,
mirrors the existing "always run Company" pattern), or explicitly document a periodic
force-full-resync as the intended staleness mitigation.

## Notes for testing

The `syncCursorUtc` keyset value is a Postgres **stored generated column**
(`GENERATED ALWAYS AS COALESCE("UpdatedAtUtc","CreatedAtUtc") STORED`) with composite indexes
`Companies(SyncCursorUtc, Id)` and, per child level, `(CompanyId, SyncCursorUtc, Id)`. Correct,
stable, tie-broken ordering across page boundaries is verified against real PostgreSQL in
`MasterDataSyncSqlIntegrationTests` (opt-in via `IAMS_PG_TEST_CONN`); handler-level filtering,
pagination boundaries, and parent-ownership errors are covered in `MasterDataSyncHandlerTests`.
