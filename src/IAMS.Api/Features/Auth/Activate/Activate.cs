using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Auth.Activate;

// ── Contract ────────────────────────────────────────────────────────────────
// DeviceId is REQUIRED — this is the ONLY enforcement point for single-device binding, and a null
// device id cannot be bound to anything.
public record ActivateCommand(string ActivationKey, string DeviceId);

public class ActivateValidator : AbstractValidator<ActivateCommand>
{
    public ActivateValidator()
    {
        RuleFor(x => x.ActivationKey).NotEmpty().MinimumLength(8).MaximumLength(256);

        // Not device attestation — just enough shape-checking that an empty string, whitespace, or an
        // obviously-garbage value cannot be blindly trusted as "the device". The mobile client generates a
        // 32-char lowercase-hex id (see device_id_provider.dart); this is deliberately a little looser than
        // that exact shape so it does not become a silent contract-breaker if that generator ever changes.
        RuleFor(x => x.DeviceId).NotEmpty().MinimumLength(8).MaximumLength(200)
            .Matches("^[A-Za-z0-9_-]+$").WithMessage("DeviceId must be an opaque alphanumeric identifier.");
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Validates an Activation Key and enforces single-device binding, then issues a session — the ONLY
/// authentication mechanism now that username/password + 2FA/OTP are removed.
///
/// State machine (per key, i.e. per <see cref="User"/> row — there is exactly one Activation Key per user):
///   - Key doesn't resolve to an active user            → 401 <see cref="ErrorCodes.ActivationKeyInvalid"/>.
///   - Not yet activated (fresh key OR after admin reset) → bind THIS device, activate, authenticate.
///   - Activated, presented device matches bound device  → authenticate (no state change needed).
///   - Activated, presented device does NOT match         → 403 <see cref="ErrorCodes.ActivationKeyAlreadyBound"/>.
///
/// Atomicity: binding is an UPDATE to the User row (the key already exists as a column — there is nothing
/// to INSERT), so the only race that can double-bind a key is two concurrent UPDATEs to the same row. The
/// User table's PostgreSQL <c>xmin</c> system column is configured as an optimistic-concurrency token (see
/// <c>UserConfiguration</c> / <c>IamsDbContext.OnModelCreating</c> — same pattern proven on the old
/// per-user device-binding table): the loser's <see cref="DbUpdateConcurrencyException"/> is caught and the
/// loop re-reads the fresh row and re-decides against it, exactly like <c>ResetUserActivationHandler</c>'s
/// retry loop. This closes the "two devices activate the same never-used key simultaneously" race from the
/// spec's security requirements.
/// </summary>
public class ActivateHandler(
    IamsDbContext db,
    SessionIssuer sessionIssuer,
    IClock clock)
{
    /// <summary>
    /// Bounded retry count for the optimistic-concurrency loop, matching the idiom already used in
    /// <c>ResetUserActivationHandler</c> for the same xmin-backed token: contention here is expected to be
    /// rare (two near-simultaneous activation attempts on the exact same still-fresh key) and
    /// non-repeating, so a small bounded loop — not an unbounded retry — is appropriate.
    /// </summary>
    private const int MaxConcurrencyAttempts = 3;

    public async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> HandleAsync(
        ActivateCommand command, CancellationToken ct)
    {
        // Deterministic hash lookup (see User.ActivationKeyHash doc comment for why this is SHA-256, not
        // PBKDF2) — the Activation Key itself is the only row-selector available; there is no separate
        // "username" to find the user by first. The raw key is never logged or persisted.
        var keyHash = TokenGenerator.Sha256(command.ActivationKey);
        var now = clock.UtcNow;

        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.ActivationKeyHash == keyHash, ct);

            // Uniform failure for "no such key" vs "key belongs to an inactive user" — do not give an
            // unauthenticated caller holding an arbitrary string a way to distinguish the two.
            if (user is null || !user.IsActive)
            {
                return ApiError.Problem(StatusCodes.Status401Unauthorized,
                    ErrorCodes.ActivationKeyInvalid, "Invalid activation key.");
            }

            var alreadyBoundElsewhere =
                user.ActivationStatus == ActivationStatus.Activated &&
                user.ActivatedDeviceId != command.DeviceId;

            if (alreadyBoundElsewhere)
            {
                return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.ActivationKeyAlreadyBound,
                    "This activation key is already activated on another device. Please contact your administrator to reset it.");
            }

            var alreadyBoundToThisDevice = user.ActivationStatus == ActivationStatus.Activated;

            if (!alreadyBoundToThisDevice)
            {
                // Not-yet-activated (fresh key or the result of an admin reset — both look identical here):
                // bind THIS device. Clear the prior reset's audit fields — they describe the OLD
                // (now-superseded) registration, not this one.
                user.ActivationStatus = ActivationStatus.Activated;
                user.ActivatedDeviceId = command.DeviceId;
                user.ActivatedAtUtc = now.UtcDateTime;
                user.ActivationResetAtUtc = null;
                user.ActivationResetByUserId = null;

                try
                {
                    // Flush now, before resolving scope / issuing a session below, so a concurrent
                    // activation attempt on this exact key surfaces HERE as a catchable xmin conflict
                    // instead of silently double-issuing sessions for two different devices.
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
                {
                    // Lost the race: a concurrent request already activated this key (its xmin moved)
                    // between our read and our write. Detach our stale copy — never leave a stale entity
                    // tracked — and loop to re-read the fresh row and re-decide against it.
                    db.Entry(user).State = EntityState.Detached;
                    continue;
                }
            }

            // Success: either freshly activated just now, or re-authenticating from the already-bound
            // device. Every user has exactly one Company (non-nullable FK) and one role, so there is no
            // "no active company" case to guard — issue the session directly.
            var session = await sessionIssuer.IssueAsync(user, command.DeviceId, ct);

            return TypedResults.Ok(new AuthTokenResponse(
                session.AccessToken,
                "Bearer",
                session.AccessTokenExpiresAt,
                UserSummary.From(user)));
        }

        // Unreachable in practice (see MaxConcurrencyAttempts) — the loop always returns from inside on its
        // last attempt, since the catch filter excludes the final attempt and lets a real conflict there
        // propagate as a genuine 500 rather than silently giving up.
        throw new InvalidOperationException("Unreachable: retry loop must return or throw on its final attempt.");
    }
}
