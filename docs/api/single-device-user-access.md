# API Contract — Single-Device User Access / Device Binding

Owner: `dotnet-backend-engineer`. This is the authoritative contract the Flutter mobile client
implements against. Do not invent alternative shapes — if something is missing or wrong, raise it
with the Team Lead and this doc is updated first.

This is an amendment on top of `F1-F15-auth-and-tenant-connections.md` (that file is a closed
batch — see this doc for anything touching device binding). Conventions (base path, enum
serialization, ProblemDetails error shape, rate limiting) are unchanged and defined there.

## What changed

Each user account may be signed in from **exactly one device at a time**. The device is whatever
`deviceId` the mobile client sends at 2FA verify (the client already generates and persists an
opaque device id via secure storage — see `IAMS-mobile/lib/core/storage/device_id_provider.dart`).
The binding is created on a user's first successful verify and is enforced on every verify after
that.

### 1. `POST /api/auth/2fa/verify` — (anonymous) — **`deviceId` is now REQUIRED**

Request (unchanged shape, `deviceId` now required and non-empty):
```json
{ "challengeToken": "opaque-token", "code": "123456", "deviceId": "device-1" }
```

- Omitting `deviceId`, or sending `null`/`""`, now fails validation: `400 validation_failed` with
  an `errors.DeviceId` entry (same validation-error shape as every other field).

Behavior on a correct challenge + code:
- **No binding yet for this user** → this device is registered as the bound device. `200 OK` (same
  `AuthTokenResponse` shape as before).
- **Binding exists and `deviceId` matches** → `200 OK`, same as always. (Internally, the binding's
  last-authenticated timestamp is refreshed — no client-visible change.)
- **Binding exists and `deviceId` does NOT match** → **`403 Forbidden`**, new error code (see below).
  The OTP challenge is consumed by this rejection (credentials + OTP were correct), so the client
  must restart from `/api/auth/login` to get a fresh challenge — it cannot retry verify with the
  same challenge token from a different device.
- A binding that an admin has reset behaves exactly like "no binding yet" — the next device to
  verify successfully becomes the new bound device.

New error response for a device mismatch:
```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "This account is already registered to another device. Please contact your administrator to reset your device registration.",
  "status": 403,
  "code": "device_already_registered"
}
```

| `code` | Status | Meaning / client action |
|---|---|---|
| `device_already_registered` | 403 | This account is bound to a different device. Show a dedicated "already registered on another device" message (do not treat as a generic `access_denied`/403). Recovery requires an admin to reset the binding out-of-band; the client should not auto-retry. |

Full updated error list for this endpoint: `400 validation_failed`, `401 two_factor_invalid`,
`401 two_factor_expired`, `429 two_factor_locked`, `403 no_active_company`,
**`403 device_already_registered` (new)**.

### 2. `POST /api/auth/login` and `POST /api/auth/refresh` — **unchanged**

`deviceId` remains optional on both `LoginCommand` and `RefreshTokenCommand`. Device-binding
enforcement happens ONLY at 2FA verify, not at login or refresh — login never issues a session, and
refresh reuses the session's already-approved device. There is no request/response shape change on
either endpoint.

### 3. `POST /api/admin/users/{userId}/device-binding/reset` — admin-only (informational; not called by mobile)

`[Authorize(Policy = "SystemAdmin")]` — requires a JWT with the `is_system_admin` claim set to
`"true"`. There is currently no way to grant this via the API; it is set directly on `Users.IsSystemAdmin`
in the database. No web/mobile UI exists for this in Phase 1 — exercised via Swagger/Postman only.

Request: no body; `userId` is a route parameter (GUID).

`200 OK`:
```json
{ "userId": "00000000-0000-0000-0000-000000000000", "resetAtUtc": "2026-09-13T01:34:06.000+00:00" }
```

Errors:
- `404 not_found` — this user has no active device binding to reset.
- `401` (no/invalid token) or `403` (authenticated but not a system admin) — standard ASP.NET Core
  authorization failures for a policy-gated endpoint; no custom `code` body on these two.

The reset does not delete any row — it flips the existing binding to a `Reset` state (audit trail
preserved: original `deviceId`, `registeredAtUtc`, plus the new `resetAtUtc`/`resetByUserId`). The
next successful 2FA verify for that user re-registers whatever device authenticates.
