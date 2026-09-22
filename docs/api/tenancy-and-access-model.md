# API Contract — Tenancy & Access Model (Company + Role + Assigned Locations)

Owner: `dotnet-backend-engineer`. Authoritative contract the Angular (`IAMS-web`) and Flutter
(`IAMS-mobile`) clients implement against. Supersedes the Tenant/cross-company-connection contract in
`docs/api/F1-F15-auth-and-tenant-connections.md` (kept only as history).

- Base path: `/api`. JSON request/response bodies (`Content-Type: application/json`).
- Authenticated endpoints require `Authorization: Bearer <accessToken>`.
- Timestamps are ISO-8601 with offset (e.g. `2026-09-22T12:34:56.789+00:00`).
- Errors use ProblemDetails with a machine-readable `code` extension, e.g.
  `{ "title": "...", "status": 404, "code": "not_found" }`. Validation errors (`400`) additionally carry
  the standard `errors` map. Known codes: `validation_failed`, `not_found`, `access_denied`,
  `activation_key_invalid`, `activation_key_already_bound`, `stock_version_conflict`, `insufficient_stock`,
  `stock_count_not_pending`.

## Roles

Fixed, closed set of int-coded roles (higher code = strictly more privilege). Each user has exactly one.

| Name | Code |
|---|---|
| `SuperAdmin` | 800 |
| `Admin` | 700 |
| `Manager` | 300 |
| `User` (a.k.a. "Scanner") | 200 |
| `Viewer` | 100 |

**Wire shape of a role** — emitted everywhere a role appears, carrying BOTH forms so clients need not
hardcode name strings and can compare privilege numerically:

```json
"role": { "code": 700, "name": "Admin" }
```

Access scoping (enforced server-side on every data endpoint):

- **SuperAdmin** — full system access; not restricted by Company or Location.
- **Admin** — own Company, all its Locations; may manage that Company's users.
- **Manager / User / Viewer** — own Company, restricted to their **assigned Locations** only (no bleed into
  unassigned Locations of the same Company). Viewer is read-only; User/Scanner may perform inventory/scan
  writes; Manager may additionally approve/reject stock counts.

## Authentication — `POST /api/auth/activate`

Unchanged flow (Activation Key + single-device binding; see `activation-key-authentication.md`), but the
returned `user` now carries the Company and role instead of any tenant info.

Request: `{ "activationKey": "<string>", "deviceId": "<opaque id>" }`

`200 OK`:

```json
{
  "accessToken": "<jwt>",
  "tokenType": "Bearer",
  "accessTokenExpiresAt": "2126-09-22T00:00:00+00:00",
  "user": {
    "id": "0b1e...", "username": "alice", "displayName": "alice",
    "companyId": "9f3c...",
    "role": { "code": 200, "name": "User" }
  }
}
```

Errors: `400 validation_failed`, `401 activation_key_invalid`, `403 activation_key_already_bound`.
**Removed:** the former `403 no_active_company` — every user now has a Company by construction, so that
branch no longer exists.

The JWT carries claims `sub`, `company_id`, `role` (the int code, e.g. `"700"`), `session_id`, `username`.
(Removed vs. before: `tenant_id`, `location_id`, `is_system_admin`.)

## Effective scope — `GET /api/me/scope`

Auth required (any role). Returns the caller's Company, role, and the Locations they may act within.

`200 OK`:

```json
{
  "user": { "id": "...", "username": "alice", "displayName": "alice" },
  "role": { "code": 200, "name": "User" },
  "company": { "id": "9f3c...", "name": "Acme" },
  "assignedLocations": [ { "id": "...", "name": "Warehouse North" } ],
  "unrestrictedCompanyAccess": false,
  "systemWideAccess": false
}
```

- `assignedLocations` — for Manager/User/Viewer, exactly their assigned set; for Admin/SuperAdmin, **every
  Location in their Company**.
- `unrestrictedCompanyAccess` — `true` for Admin and SuperAdmin (see all Company Locations).
- `systemWideAccess` — `true` only for SuperAdmin (not confined to a single Company).

## User ↔ Location assignment (new) — `Admin`+ only

Both endpoints require role ≥ Admin (`403` otherwise via policy). An **Admin** may only target users in
their **own Company** (a user elsewhere returns `404`); **SuperAdmin** may target any Company.

### `PUT /api/users/{userId}/locations` — replace the user's assigned-Location set

Request: `{ "locationIds": ["<guid>", "<guid>"] }` (order-independent; an **empty array clears** all
assignments; duplicates rejected).

`200 OK`:

```json
{ "userId": "...", "locations": [ { "id": "...", "name": "Warehouse North" } ] }
```

Errors:
- `400 validation_failed` — duplicate ids, empty-guid ids, or any `locationId` that does not exist in the
  target user's Company (the body's `invalidLocationIds` extension lists the offenders).
- `404 not_found` — user does not exist, or (for an Admin caller) is in a different Company.
- `403 access_denied` — caller's role is below Admin.

### `GET /api/users/{userId}/locations` — list the user's assigned Locations

`200 OK`: same body shape as the PUT response. `404`/`403` as above.

## Scoping applied to existing endpoints

No route or request shape changed for the master-data and inventory endpoints; only the **scoping** and the
**DTO fields** changed:

- **Removed `tenantId`** from every DTO (`CompanyDto`, `LocationDto`, `WarehouseDto`, `RackDto`, `BinDto`,
  inventory item/stock DTOs, item detail). Clients must drop that field.
- `GET /api/master-data/companies` — returns the caller's own Company only (SuperAdmin: all Companies).
- `GET /api/master-data/{locations|warehouses|racks|bins}` — Company-scoped, and further narrowed to the
  caller's assigned Locations for Manager/User/Viewer. A `parentId` that is out of the caller's scope now
  returns `403 access_denied` (previously "unreachable connection"); a non-existent one still `404 not_found`.
- Inventory: the SKU catalog (`/api/inventory/items`, sync feed `/api/inventory/sync/items`, scan SKU
  resolution) is **Company-scoped**; physical/stock data (stock levels, stock-by-bin, stock counts, bin scan
  resolution, movements' bins) is **Location-scoped** to the assigned set. `ListInventory` aggregate on-hand
  and item-detail per-bin quantities reflect only the caller's Locations. Scanning a code that resolves only
  outside the caller's scope returns `Blocked` with a null id (unchanged BR-003 behavior, now scope-based).
- Inventory **write** endpoints (`receive`/`transfer`/`adjust`/`stock-counts` create) require role ≥ User;
  stock-count **approve/reject** require role ≥ Manager. A Viewer receives `403` on any of these.
- Admin activation reset `POST /api/admin/users/{userId}/activation/reset` now requires role ≥ Admin and is
  Company-scoped for Admin callers (target in another Company → `404`).
