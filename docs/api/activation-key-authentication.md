# API Contract — Activation Key Authentication

Owner: `dotnet-backend-engineer`. This is the authoritative contract the Flutter mobile client
implements against. Do not invent alternative shapes — if something is missing or wrong, raise it
with the Team Lead and this doc is updated first.

This **replaces** `F1-F15-auth-and-tenant-connections.md`'s login/2FA sections and the entire
`single-device-user-access.md` doc. Those two files are left in place as a historical record —
**do not implement against them.** Conventions unchanged from those docs and still in force here:
base path `/api`, enums serialized as strings, ProblemDetails error shape with a stable `code`,
ISO-8601 timestamps with offset, per-IP rate limiting on unauthenticated auth endpoints.

## What was removed

- `POST /api/auth/login` — **gone**. There is no username/password step anymore.
- `POST /api/auth/2fa/verify` — **gone**. There is no 2FA/OTP step anymore.
- `POST /api/auth/2fa/resend` — **gone**, for the same reason.
- The mobile "Enter Activation Key" screen is now the **only** entry point to authentication —
  there is no username/password field anywhere in the login UI.

`POST /api/auth/refresh`, `POST /api/auth/logout`, `GET /api/me/scope`, and the F15 access
endpoints (`/api/access/evaluate`, `/api/access/evaluate-batch`) are **unchanged** — see
`F1-F15-auth-and-tenant-connections.md` for those; only login/2FA moved.

## What's new

### 1. `POST /api/auth/activate` — (anonymous)

The only authentication entry point. Validates an Activation Key, enforces single-device binding,
and — on success — issues the same session shape login+2FA used to issue.

Request:
```json
{ "activationKey": "the-users-activation-key", "deviceId": "device-1" }
```
- `activationKey` — **required**, 8–256 chars. Never logged, never echoed back in any response.
- `deviceId` — **required** (unlike the old `LoginCommand`/`RefreshTokenCommand`, where it was
  optional) — this is the single enforcement point for device binding, and there is nothing to
  bind a null device to. 8–200 chars, `^[A-Za-z0-9_-]+$`. The mobile client already generates and
  persists an opaque per-install id via secure storage
  (`IAMS-mobile/lib/core/storage/device_id_provider.dart`, currently a 32-char lowercase-hex
  string) — send that value as-is. The server does not perform device attestation; this is
  shape-validation only (reject empty/garbage input), not a hardware-identity guarantee.

Behavior on a valid key:
- **Not yet activated** (fresh key, or an admin has reset it) → this device is bound as the
  Activation Key's device. `200 OK` with a fresh session (**AuthTokenResponse**, same shape as the
  old `2fa/verify` response).
- **Already activated, presented device matches the bound device** → `200 OK`, same shape — a
  normal re-authentication. No visible state change.
- **Already activated, presented device does NOT match** → **`403 Forbidden`**,
  `activation_key_already_bound`. The client should show a dedicated "already registered on
  another device" message and must not auto-retry; recovery requires an admin to reset the
  binding out-of-band (see below).

`200 OK` → **AuthTokenResponse**:
```json
{
  "accessToken": "jwt...",
  "tokenType": "Bearer",
  "accessTokenExpiresAt": "2026-09-14T12:49:56+00:00",
  "refreshToken": "opaque-refresh-token",
  "refreshTokenExpiresAt": "2026-10-14T12:34:56+00:00",
  "user": { "id": "guid", "username": "alice", "displayName": "alice" }
}
```
Byte-for-byte the same `AuthTokenResponse` shape as the old `2fa/verify`/`refresh` responses —
`user.username`/`displayName` are now purely display fields (there is no username-based login to
break). Access token lifetime 15 min, refresh 30 days (configurable, unchanged). After activating,
call `GET /api/me/scope` for the full effective scope, exactly as before.

Errors:

| `code` | Status | Meaning / client action |
|---|---|---|
| `validation_failed` | 400 | Missing/malformed `activationKey` or `deviceId`; show field errors. |
| `activation_key_invalid` | 401 | The key does not resolve to an active user. **Deliberately the same code** whether the key doesn't exist at all or belongs to a deactivated account — do not attempt to distinguish these in the UI beyond "that key isn't valid." |
| `activation_key_already_bound` | 403 | Key is valid but bound to a different device. Show the dedicated "registered on another device" message; do not auto-retry. |
| `no_active_company` | 403 | The key is valid and this device is now bound, but the user has no company membership to sign in to. |

> **Rate limiting:** `/api/auth/activate` is throttled per client IP (10 requests/minute, same
> policy as the old login/2fa endpoints) — exceeding it returns a bare `429` with no `code` body.

### 2. `POST /api/admin/users/{userId}/activation/reset` — admin-only (informational; not called by mobile)

Replaces `POST /api/admin/users/{userId}/device-binding/reset` (same authorization pattern, new
path/domain). `[Authorize(Policy = "SystemAdmin")]` — requires a JWT with the `is_system_admin`
claim set to `"true"`. No web/mobile UI exists for this yet — exercised via Swagger/Postman only.
There is still no API to grant `IsSystemAdmin` or to provision a user's Activation Key; both remain
set directly in the database, same as before.

Request: no body; `userId` is a route parameter (GUID).

`200 OK`:
```json
{ "userId": "00000000-0000-0000-0000-000000000000", "resetAtUtc": "2026-09-14T01:34:06.000+00:00" }
```

Errors:
- `404 not_found` — user not found, or this user has no active activation to reset.
- `401` (no/invalid token) or `403` (authenticated but not a system admin) — standard ASP.NET Core
  authorization failures; no custom `code` body on these two.

The reset flips the user's activation state back to not-activated but does **not** immediately
clear the previously-bound device id/timestamp — those are kept as an audit trail (alongside the
new reset timestamp/admin id) until the next successful `POST /api/auth/activate` overwrites them.
The same Activation Key can then be activated again, on whichever device presents it next.

**The reset also immediately cuts off the old device's existing session** — this is the whole point
of a reset (e.g. "device lost/stolen"): the user's `SecurityStamp` is rotated and every currently-active
`UserSession` for that user is revoked, so the old device's refresh token can no longer renew itself
(its next `POST /api/auth/refresh` gets `401 session_expired`). An already-issued access token that
hasn't yet expired is not retroactively revoked (JWTs are stateless, ≤15 min lifetime) — same as every
other forced-logout path in this API.

## Data model (for context — not part of the wire contract)

Activation/device-binding fields now live directly on the `Users` table (not a side table):
`ActivationKeyHash` (SHA-256 hash of the key — the raw key is never stored), `ActivationStatus`
(`NotActivated` | `Activated`), `ActivatedDeviceId`, `ActivatedAtUtc`, `ActivationResetAtUtc`,
`ActivationResetByUserId`. `Users.Username`/`Email` are now display/contact-only — neither is used
to authenticate. `Users.NormalizedUsername` and `Users.PasswordHash` are gone. See
`docs/schema/README.md`'s 2026-09-14 callout for the full migration note.
