# API Contract — F1 Authentication & 2FA + F15 Effective Access (Phase 1)

> **SUPERSEDED (2026-09-22) — the Tenant / cross-company-connection model this document describes has
> been removed entirely.** `Tenant`, `CompanyConnection`/`Scope`/`Filter`, the `ConnectionType`/`TenantKind`
> enums, the `Role` lookup table, and `UserCompanyMembership` are gone. `Company` is now the top-level unit;
> a user belongs to exactly one Company via a direct `CompanyId` FK, holds one int-coded `UserRole`
> (`SuperAdmin=800, Admin=700, Manager=300, User=200, Viewer=100`), and is scoped to a set of assigned
> Locations. This file is kept as a **historical record** of the superseded login/2FA/connection contract and
> is **not** updated in place — see **`docs/api/tenancy-and-access-model.md`** for the current contract and
> `docs/api/activation-key-authentication.md` for the (still-current) activation flow.

Owner: `dotnet-backend-engineer`. This is the authoritative contract the Flutter mobile client
implements against. Do not invent alternative shapes — if something is missing or wrong, raise it
with the Team Lead and this doc is updated first.

- Base path: `/api`
- All request/response bodies are JSON (`Content-Type: application/json`).
- **Enums are serialized as strings** (e.g. `"Read"`, `"Full"`, `"ParentToChild"`, `"ConnectionGranted"`).
- Authenticated endpoints require `Authorization: Bearer <accessToken>`.
- Timestamps are ISO-8601 with offset (e.g. `2026-09-09T12:34:56.789+00:00`).

## Error shape (all 4xx/5xx)

RFC 7807 ProblemDetails with a stable machine-readable `code` the client should branch on
(never string-match the human `title`/`detail`):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Invalid username or password.",
  "status": 401,
  "code": "invalid_credentials"
}
```

Validation errors (400) additionally carry an `errors` map:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "code": "validation_failed",
  "errors": { "Username": ["'Username' must not be empty."] }
}
```

| `code` | Typical status | Meaning / client action |
|---|---|---|
| `validation_failed` | 400 | Bad request body; show field errors. |
| `invalid_credentials` | 401 | Wrong username/password. |
| `account_inactive` | 401 | Account disabled. |
| `two_factor_invalid` | 401 | Wrong/used challenge or wrong OTP. |
| `two_factor_expired` | 401 | OTP window elapsed; restart login. |
| `two_factor_locked` | 429 | Too many wrong OTP attempts; restart login. |
| `resend_too_soon` | 429 | Resend requested before cooldown. |
| `resend_limit_reached` | 429 | Total resends for this challenge exhausted; restart login. |
| `session_expired` | 401 | Refresh token invalid/expired/revoked → **Session Expired screen**. |
| `no_active_company` | 403 | 2FA succeeded but the user has no company membership to sign in to. |
| `access_denied` | 403 | Reserved for cross-tenant denial on resource endpoints (future). |
| `not_found` | 404 | Challenge/user not found. |

> Note: ASP.NET Core also adds a `traceId` field to every ProblemDetails body — informational, safe to ignore.

> **Rate limiting:** the unauthenticated auth endpoints (`/auth/login`, `/auth/2fa/verify`,
> `/auth/2fa/resend`) are throttled per client IP (10 requests/minute). Exceeding it returns a bare
> `429` (no `code` body). Back off and retry after the window.

---

## F1 — Authentication & 2FA

### 1. `POST /api/auth/login` — (anonymous)

Verifies credentials and **always** starts a 2FA challenge (login never returns tokens directly).

Request:
```json
{ "username": "alice", "password": "s3cret", "deviceId": "optional-device-id" }
```

`200 OK`:
```json
{
  "challengeToken": "opaque-token",
  "expiresInSeconds": 300,
  "resendAvailableInSeconds": 30,
  "devOtp": "123456"
}
```
- `challengeToken` — carry to the verify/resend calls.
- `devOtp` — **non-production only** (the OTP, so the mobile team can test end-to-end). It is
  `null` in Production; the real OTP is delivered out of band (delivery mechanism is "Not Specified"
  in the source and not built in Phase 1).

Errors: `400 validation_failed`, `401 invalid_credentials`, `401 account_inactive`.

### 2. `POST /api/auth/2fa/verify` — (anonymous)

Request:
```json
{ "challengeToken": "opaque-token", "code": "123456", "deviceId": "optional-device-id" }
```

`200 OK` → the session (see **AuthTokenResponse** below).

Errors: `400 validation_failed`, `401 two_factor_invalid`, `401 two_factor_expired`,
`429 two_factor_locked`, `403 no_active_company`.

### 3. `POST /api/auth/2fa/resend` — (anonymous)

Rotates the OTP and extends the window. Subject to a cooldown.

Request:
```json
{ "challengeToken": "opaque-token" }
```

`200 OK`:
```json
{
  "challengeToken": "opaque-token",
  "expiresInSeconds": 300,
  "resendAvailableInSeconds": 30,
  "devOtp": "654321"
}
```

Errors: `400 validation_failed`, `404 not_found`, `429 resend_too_soon`, `429 resend_limit_reached`.

### 4. `POST /api/auth/refresh` — (anonymous)

Exchanges a refresh token for a fresh session (rotating: the presented refresh token is revoked).

Request:
```json
{ "refreshToken": "opaque-refresh-token", "deviceId": "optional-device-id" }
```

`200 OK` → **AuthTokenResponse**.

Errors: `400 validation_failed`, `401 session_expired` → drive the **Session Expired** screen.

### 5. `POST /api/auth/logout` — (**Bearer**)

Revokes the supplied refresh token (idempotent).

Request:
```json
{ "refreshToken": "opaque-refresh-token" }
```

`204 No Content`.

### AuthTokenResponse (returned by verify + refresh)

```json
{
  "accessToken": "jwt...",
  "tokenType": "Bearer",
  "accessTokenExpiresAt": "2026-09-09T12:49:56+00:00",
  "refreshToken": "opaque-refresh-token",
  "refreshTokenExpiresAt": "2026-10-09T12:34:56+00:00",
  "user": { "id": "guid", "username": "alice", "displayName": "alice" }
}
```
> `displayName` currently mirrors `username` (the user record has no separate display name yet); the
> field is stable and will carry a real display name later without a shape change.
Access token lifetime 15 min, refresh 30 days (configurable). The JWT embeds the user's active
`tenant_id` / `company_id` / `location_id` claims. After login, call `GET /api/me/scope` for the
full effective scope (connections + permissions).

---

## F1/F15 — Effective scope

### 6. `GET /api/me/scope` — (**Bearer**)

Resolves the caller's active tenant/company/location plus every connection configured **from their
active company**, with permission level and hierarchy scope.

`200 OK`:
```json
{
  "user": { "id": "guid", "username": "alice", "displayName": "alice" },
  "activeTenant": { "id": "guid", "name": "Parent Co", "kind": "Parent" },
  "activeCompany": { "id": "guid", "name": "Company A", "tenantId": "guid" },
  "activeLocation": { "id": "guid", "name": "Main Depot" },
  "connections": [
    {
      "connectionId": "guid",
      "targetCompany": { "id": "guid", "name": "Company B", "tenantId": "guid" },
      "targetTenant": { "id": "guid", "name": "Child Co", "kind": "Child" },
      "connectionType": "ParentToChild",
      "isEnabled": true,
      "permissionLevel": "Write",
      "scopes": [
        { "level": "Warehouse", "nodeId": "guid", "permissionLevel": "Full" },
        { "level": "Location",  "nodeId": "guid", "permissionLevel": "Read" }
      ],
      "policyVersion": 12
    }
  ],
  "policyVersion": 12
}
```
- `activeLocation` may be `null` (whole-company scope).
- A connection has **one or more `scopes`** (a single connection can grant several hierarchy nodes).
  Each scope: `level` ∈ `Company | Location | Warehouse | Rack | Bin`, `nodeId` = the id of the node
  at that level, and `permissionLevel` = the scope's effective permission (its own override, else the
  connection's `permissionLevel`). A `Company`-level scope's `nodeId` is the target company id (grants
  the whole company). An empty `scopes` array means the connection grants nothing yet.
- `permissionLevel` (on the connection) is the connection default; per-scope `permissionLevel` is what
  actually applies to that node.
- `tenant` objects carry `kind` (`Parent` | `Child`), not `type`.
- `policyVersion` — max version across the caller's connections. Store it; a change means the
  connection set/policy changed (BR-TC-007) and cached scope should be refreshed.

---

## F15 — Effective access evaluation

The connection policy is enforced **server-side**. The client uses these endpoints to (a) decide UI
affordances (allowed / read-only / denied) before opening a cross-tenant resource (F3/F4/F5…), and
(b) re-validate its offline queue at sync time (F11, BR-TC-008).

### Permission model
`Read < Write < Full`. A requirement is met when the effective permission ≥ required. Same-tenant
access is always allowed here (normal tenant authorization applies and is out of scope for the
connection policy).

### 7. `POST /api/access/evaluate` — (**Bearer**)

Evaluate one resource.

Request:
```json
{
  "resource": {
    "companyId": "guid-of-target-company",
    "locationId": "guid-or-null",
    "warehouseId": "guid-or-null",
    "rackId": "guid-or-null",
    "binId": "guid-or-null",
    "resourceType": "Asset",
    "resourceId": "optional-opaque-ref"
  },
  "requiredPermission": "Write"
}
```
- `companyId` is **required**; the deeper hierarchy ids are the resource's coordinates. Provide as
  many as the resource resolves to — a connection scoped deeper than the coordinates you supply
  **fails closed** (denied `OutOfScope`).
- The server derives the target tenant from `companyId`; do not send a tenant id.

`200 OK`:
```json
{
  "decision": "Allowed",
  "effectivePermission": "Full",
  "reason": "ConnectionGranted",
  "connectionId": "guid",
  "policyVersion": 12
}
```
- `decision` ∈ `Allowed | Denied`.
- `reason` ∈ `SameTenant | ConnectionGranted | NoConnection | ConnectionDisabled | OutOfScope | InsufficientPermission`.
  Map denial reasons to the F15 **Access Denied** states:
  `ConnectionDisabled → disabled-connection`, `OutOfScope → outside-location-scope`,
  `InsufficientPermission → insufficient-permission`, `NoConnection → (no connected company)`.
- `effectivePermission` ∈ `None | Read | Write | Full` (best available even when denied for
  insufficient permission — lets the client offer read-only if it asked for Write).

Errors: `400 validation_failed`.

### 8. `POST /api/access/evaluate-batch` — (**Bearer**)

Evaluate up to **500** resources in one call. This is the sync-time re-validation primitive for the
offline queue (F11, BR-TC-008): send every queued mutation's target + required permission; reject
locally any item that comes back `Denied`.

Request:
```json
{
  "items": [
    {
      "clientRef": "queued-op-id-1",
      "resource": { "companyId": "guid", "warehouseId": "guid" },
      "requiredPermission": "Write"
    },
    {
      "clientRef": "queued-op-id-2",
      "resource": { "companyId": "guid" },
      "requiredPermission": "Read"
    }
  ]
}
```

`200 OK`:
```json
{
  "policyVersion": 12,
  "evaluatedAtUtc": "2026-09-09T12:40:00+00:00",
  "results": [
    {
      "clientRef": "queued-op-id-1",
      "decision": {
        "decision": "Denied",
        "effectivePermission": "Read",
        "reason": "InsufficientPermission",
        "connectionId": "guid",
        "policyVersion": 12
      }
    },
    {
      "clientRef": "queued-op-id-2",
      "decision": {
        "decision": "Allowed",
        "effectivePermission": "Full",
        "reason": "SameTenant",
        "connectionId": null,
        "policyVersion": null
      }
    }
  ]
}
```
- `clientRef` is echoed back so you can correlate each decision to its queued op; results preserve
  request order.
- Top-level `policyVersion` is the version the batch was validated against — persist it with the
  queue so you can detect drift before the next sync.

Errors: `400 validation_failed` (empty batch, >500 items, missing `companyId`, or bad
`requiredPermission`).

---

## Changed since the first draft (schema reconciliation — mobile please note)

Only **`GET /api/me/scope`** changed shape; `login`, `2fa/verify`, `2fa/resend`, `refresh`, `logout`,
`access/evaluate`, and `access/evaluate-batch` are byte-for-byte as first published. The three deltas:

1. **`/me/scope` connections** — a connection's single `scopeLevel`/`scopeNodeId` became a **`scopes[]`
   array** (`{ level, nodeId, permissionLevel }`), because one connection can grant several hierarchy
   nodes (the schema models scopes as a child collection). `policyVersion` is unchanged.
2. **`/me/scope` tenant objects** use **`kind`** (`Parent`/`Child`), not `type`.
3. New error code **`no_active_company` (403)** on `2fa/verify`; `user.displayName` mirrors `username`
   for now. Neither changes an existing field's type.

## Open items (flagged to Team Lead / DB engineer)

- **One connection per directed company pair** (unique index) — so precedence "across multiple
  connections" is moot. Where a single connection has multiple applicable **scopes**, the effective
  permission is **most-permissive-wins** across them (each scope's override, else the connection
  default). Changeable in one place (`EffectiveAccessEvaluator`).
- **Hierarchy scope is inherited downward** (a warehouse scope covers its racks/bins) via the
  resource's denormalized ancestry.
- **Resource coordinates are client-supplied in Phase 1** (no asset/inventory tables yet). When
  those tables land, the server should resolve a resource's hierarchy itself rather than trust the
  client. Until then `evaluate` is a policy primitive, not a resource authority.
- **`policyVersion`** on the wire maps to each connection's `PolicyRevision` (a monotonic counter the
  backend bumps atomically on any policy-affecting change). The top-level value is the max across the
  caller's connections. Opaque monotonic number — store and compare, don't interpret.
- Offline-login policy and account lockout are **Not Specified** in the source and intentionally not
  built; the schema/endpoints leave room to add them (e.g. failed-attempt columns) without contract
  changes.
