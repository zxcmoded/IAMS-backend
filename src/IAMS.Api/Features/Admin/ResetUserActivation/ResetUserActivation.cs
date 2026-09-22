using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Admin.ResetUserActivation;

// ── Contract ────────────────────────────────────────────────────────────────
public record ResetUserActivationCommand(Guid UserId);

public record ResetUserActivationResponse(Guid UserId, DateTimeOffset ResetAtUtc);

public class ResetUserActivationValidator : AbstractValidator<ResetUserActivationCommand>
{
    // Deliberately NO NotEmpty() rule on UserId: Guid.Empty is a syntactically valid (if never-real)
    // user id, and rejecting it here would return 400 validation_failed instead of the documented 404
    // not_found that every other non-existent id already gets from the handler below — a real id that
    // happens to not exist and the literal all-zero guid should 404 identically, not take two different
    // codes depending on which non-existent id was presented.
    public ResetUserActivationValidator()
    {
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Platform-admin-only reset of a user's Activation Key device binding (see
/// <see cref="User.ActivationStatus"/>). Flips <see cref="ActivationStatus.Activated"/> back to
/// <see cref="ActivationStatus.NotActivated"/> — deliberately does NOT clear
/// <see cref="User.ActivatedDeviceId"/>/<see cref="User.ActivatedAtUtc"/> immediately, so the prior
/// registration remains as an audit trail (alongside the new <see cref="User.ActivationResetAtUtc"/>/
/// <see cref="User.ActivationResetByUserId"/>) until the next successful activation overwrites them. The
/// same Activation Key can then be activated again, on whichever device presents it next (see
/// ActivateHandler).
///
/// Also performs the session/security bookkeeping this codebase has for an admin reset (e.g. "device
/// lost/stolen"), which serves audit + blocks the old device from re-activating the key — but does NOT
/// cut off the old device's live access:
///   - Rotates <see cref="User.SecurityStamp"/> and flips <see cref="ActivationStatus"/> back to
///     <see cref="ActivationStatus.NotActivated"/>, so the old device cannot re-activate/re-bind the key
///     again without an admin, and any future session-issue snapshots the new stamp.
///   - Marks (<see cref="UserSession.RevokedAtUtc"/>) every currently-active session for this user revoked,
///     the same pattern <c>LogoutHandler</c> uses — retained as an audit record of the reset.
/// IMPORTANT: with permanent, stateless access tokens (there is no refresh flow, and JWTs are not
/// re-validated against the DB per request), NONE of the above invalidates an access token already issued
/// to the old device — that token keeps authenticating until its (far-future) expiry. This handler's
/// revocation is therefore an audit/bookkeeping action plus a block on future re-activation, NOT an
/// immediate cut-off of the old device's live access. That enforcement gap is an accepted property of the
/// permanent-token design, not something this handler can or should work around.
/// </summary>
public class ResetUserActivationHandler(IamsDbContext db, ICurrentUser currentUser, IClock clock)
{
    /// <summary>
    /// Bounded retry count for the optimistic-concurrency loop below, guarding the User row's
    /// PostgreSQL <c>xmin</c>-backed concurrency token (see <c>IamsDbContext.OnModelCreating</c>) — a small
    /// loop rather than a single retry, matching the idiom already used here before the activation-key
    /// migration: an admin's reset action is intentional and should not give up after just one unlucky
    /// overlap with a concurrent re-activation from the user's own already-bound device (which only
    /// touches device-binding fields, so contention is expected to be rare and non-repeating in practice).
    /// </summary>
    private const int MaxConcurrencyAttempts = 3;

    public async Task<Results<Ok<ResetUserActivationResponse>, ProblemHttpResult>> HandleAsync(
        ResetUserActivationCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;

        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);

            // An Admin may only reset users in their own Company; a target elsewhere is "not found" to them.
            // SuperAdmin may reset any user.
            if (user is null ||
                (currentUser.Role != UserRole.SuperAdmin && user.CompanyId != currentUser.CompanyId))
            {
                return ApiError.Problem(StatusCodes.Status404NotFound,
                    ErrorCodes.NotFound, "User not found.");
            }

            if (user.ActivationStatus != ActivationStatus.Activated)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound,
                    ErrorCodes.NotFound, "This user has no active activation to reset.");
            }

            user.ActivationStatus = ActivationStatus.NotActivated;
            user.ActivationResetAtUtc = now.UtcDateTime;
            user.ActivationResetByUserId = currentUser.UserId;

            // Rotate the stamp and revoke sessions for audit + to block the old device from re-activating
            // (see the class doc comment — this does NOT invalidate an already-issued access token).
            // Re-queried fresh on every loop iteration, same as `user` above, so a retry after a lost
            // concurrency race re-revokes against current state rather than a stale view.
            user.SecurityStamp = Guid.NewGuid().ToString("N");

            var activeSessions = await db.UserSessions
                .Where(s => s.UserId == user.Id && s.RevokedAtUtc == null)
                .ToListAsync(ct);
            foreach (var session in activeSessions)
            {
                session.RevokedAtUtc = now.UtcDateTime;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(new ResetUserActivationResponse(command.UserId, now));
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                // The row's xmin-backed concurrency token moved, so a concurrent write to this exact row
                // (most plausibly: the user's own re-activation from the already-bound device, which
                // touches no activation-state field) can race an admin reset here too. Detach our stale
                // copy and loop: re-read fresh state and retry — if it's still Activated (the common case),
                // the reset proceeds; if it's no longer there, the loop's own check above returns 404.
                db.Entry(user).State = EntityState.Detached;
            }
        }

        // Unreachable in practice (see MaxConcurrencyAttempts) — the loop always returns from inside on its
        // last attempt, since the catch filter excludes the final attempt and lets a real conflict there
        // propagate as a genuine 500 rather than silently giving up.
        throw new InvalidOperationException("Unreachable: retry loop must return or throw on its final attempt.");
    }
}
