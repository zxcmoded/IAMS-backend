using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Admin.ResetUserDeviceBinding;

// ── Contract ────────────────────────────────────────────────────────────────
public record ResetUserDeviceBindingCommand(Guid UserId);

public record ResetUserDeviceBindingResponse(Guid UserId, DateTimeOffset ResetAtUtc);

public class ResetUserDeviceBindingValidator : AbstractValidator<ResetUserDeviceBindingCommand>
{
    public ResetUserDeviceBindingValidator() => RuleFor(x => x.UserId).NotEmpty();
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Platform-admin-only reset of a user's single-device binding (see <see cref="UserDeviceBinding"/>).
/// Flips the existing ACTIVE binding to <see cref="DeviceBindingStatus.Reset"/> — never deletes the row,
/// so the prior registration remains as an audit trail. The next successful 2FA verify for that user then
/// re-registers whichever device authenticates (see VerifyTwoFactorHandler).
/// </summary>
public class ResetUserDeviceBindingHandler(IamsDbContext db, ICurrentUser currentUser, IClock clock)
{
    /// <summary>
    /// Bounded retry count for the optimistic-concurrency loop below — matches the "detach and resolve
    /// against fresh state" idiom used throughout VerifyTwoFactorHandler for the same
    /// <see cref="UserDeviceBinding.RowVersion"/> token, but as a small loop rather than a single retry:
    /// an admin's revoke action is intentional and should not give up after just one unlucky overlap with a
    /// concurrent re-verify (which only ever touches LastAuthenticatedAtUtc, so it's expected to be rare and
    /// non-repeating in practice — 3 attempts is comfortably more than that needs, not a sign contention is
    /// expected to be sustained here).
    /// </summary>
    private const int MaxConcurrencyAttempts = 3;

    public async Task<Results<Ok<ResetUserDeviceBindingResponse>, ProblemHttpResult>> HandleAsync(
        ResetUserDeviceBindingCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;

        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            var binding = await db.UserDeviceBindings.FirstOrDefaultAsync(
                b => b.UserId == command.UserId && b.Status == DeviceBindingStatus.Active, ct);

            if (binding is null)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound,
                    ErrorCodes.NotFound, "This user has no active device registration to reset.");
            }

            binding.Status = DeviceBindingStatus.Reset;
            binding.ResetAtUtc = now.UtcDateTime;
            binding.ResetByUserId = currentUser.UserId;

            try
            {
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(new ResetUserDeviceBindingResponse(command.UserId, now));
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                // UserDeviceBinding.RowVersion is a concurrency token, so a concurrent write to this exact
                // row (most plausibly: the user's own re-verify bumping LastAuthenticatedAtUtc) can race an
                // admin reset here too. Detach our stale copy and loop: re-read fresh state and retry — if
                // it's still Active (the common case — a re-verify never changes Status away from Active),
                // the reset proceeds; if it's no longer there, the loop's own null-check above returns 404.
                db.Entry(binding).State = EntityState.Detached;
            }
        }

        // Unreachable in practice (see MaxConcurrencyAttempts) — the loop always returns from inside on its
        // last attempt, since the catch filter excludes the final attempt and lets a real conflict there
        // propagate as a genuine 500 rather than silently giving up.
        throw new InvalidOperationException("Unreachable: retry loop must return or throw on its final attempt.");
    }
}
