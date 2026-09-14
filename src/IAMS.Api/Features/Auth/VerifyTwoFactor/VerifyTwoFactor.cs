using FluentValidation;
using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAMS.Api.Features.Auth.VerifyTwoFactor;

// ── Contract ────────────────────────────────────────────────────────────────
// DeviceId is REQUIRED here (unlike LoginCommand/RefreshTokenCommand) — this is the single-device
// enforcement point, and a null device id cannot be bound to anything.
public record VerifyTwoFactorCommand(string ChallengeToken, string Code, string? DeviceId);

public class VerifyTwoFactorValidator : AbstractValidator<VerifyTwoFactorCommand>
{
    public VerifyTwoFactorValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Code).NotEmpty().Length(4, 8).Matches("^[0-9]+$")
            .WithMessage("Code must be numeric.");
        RuleFor(x => x.DeviceId).NotEmpty().MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class VerifyTwoFactorHandler(
    IamsDbContext db,
    SessionIssuer sessionIssuer,
    ActiveScopeResolver scopeResolver,
    IClock clock,
    IOptions<JwtOptions> options,
    ILogger<VerifyTwoFactorHandler> logger)
{
    /// <summary>
    /// SQL Server error numbers for a unique-index/constraint violation (2601: duplicate key on a unique
    /// index; 2627: duplicate key on a unique or primary-key constraint) — the ONLY failure the device-
    /// binding insert race is expected to produce. Anything else (deadlock, connection drop, etc.) is a
    /// real infra problem and must not be swallowed by the same handling.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sqlEx && sqlEx.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627);

    // Credentials + OTP were genuinely correct, so every rejection reachable after that point consumes the
    // challenge rather than leaving it valid for a doomed retry.
    private async Task<ProblemHttpResult> RejectDeviceMismatchAsync(OtpChallenge challenge, DateTimeOffset now, CancellationToken ct)
    {
        challenge.ConsumedAtUtc = now.UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.DeviceMismatch,
            "This account is already registered to another device. Please contact your administrator to reset your device registration.");
    }

    private async Task<ProblemHttpResult> RejectStaleChallengeAsync(OtpChallenge challenge, DateTimeOffset now, CancellationToken ct)
    {
        challenge.ConsumedAtUtc = now.UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ApiError.Problem(StatusCodes.Status401Unauthorized,
            ErrorCodes.TwoFactorInvalid, "Invalid or already-used challenge.");
    }

    /// <summary>
    /// Shared resolution for EVERY write path in <see cref="HandleAsync"/> that can lose a concurrency race
    /// (a unique-index violation on insert, or a RowVersion conflict on either update). Whoever calls this
    /// has ALREADY detached their own failed/stale tracked entity. This re-reads whatever actually landed
    /// in the database — never trusting the caller's now-stale view — and is the ONLY place that decides
    /// the outcome of a lost race, so a future write path added to this handler cannot forget the check:
    ///   - no row, or the row isn't Active (e.g. an admin reset raced in and won) → reject, stale challenge.
    ///   - row is Active but a DIFFERENT device won it → reject, device mismatch.
    ///   - row is Active with the SAME device we presented → it's a harmless same-device race (e.g. our own
    ///     retried request won); bump LastAuthenticatedAtUtc and return null to let the caller proceed.
    ///
    /// Marked <c>internal</c> (rather than a HandleAsync-local closure) specifically so tests can pin this
    /// decision logic directly — the round-3 exploit ("winner is no longer Active") can only be forced by a
    /// genuine THIRD PARTY (an admin reset) landing between a caller's read and write, a window with no
    /// awaited yield point a test can reliably land in via real concurrency; see
    /// <c>IAMS.Api.Tests.DeviceBindingTests</c> for the direct coverage this enables.
    /// </summary>
    internal async Task<ProblemHttpResult?> ResolveConcurrentWinnerAsync(
        OtpChallenge challenge, string? presentedDeviceId, DateTimeOffset now, CancellationToken ct)
    {
        var winner = await db.UserDeviceBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.UserId == challenge.UserId, ct);

        if (winner is null || winner.Status != DeviceBindingStatus.Active)
        {
            return await RejectStaleChallengeAsync(challenge, now, ct);
        }

        if (winner.DeviceId != presentedDeviceId)
        {
            return await RejectDeviceMismatchAsync(challenge, now, ct);
        }

        // Known narrow edge: this read and the caller's later flush (inside SessionIssuer.IssueAsync) are
        // not atomic, so a THIRD write landing in that gap is theoretically possible — extremely unlikely
        // in practice (it would require two overlapping conflicts on the same row back to back) and not
        // addressed here; flagged for awareness rather than blocking on it.
        var tracked = await db.UserDeviceBindings.FirstAsync(b => b.UserId == challenge.UserId, ct);
        tracked.LastAuthenticatedAtUtc = now.UtcDateTime;
        return null;
    }

    public async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> HandleAsync(
        VerifyTwoFactorCommand command, CancellationToken ct)
    {
        var challenge = await db.OtpChallenges
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.ChallengeToken == command.ChallengeToken, ct);

        if (challenge is null || challenge.IsConsumed)
        {
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.TwoFactorInvalid, "Invalid or already-used challenge.");
        }

        var now = clock.UtcNow;
        if (challenge.ExpiresAtUtc <= now.UtcDateTime)
        {
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.TwoFactorExpired, "The verification code has expired. Please sign in again.");
        }

        if (challenge.AttemptCount >= options.Value.TwoFactorMaxAttempts)
        {
            return ApiError.Problem(StatusCodes.Status429TooManyRequests,
                ErrorCodes.TwoFactorLocked, "Too many incorrect attempts. Please sign in again.");
        }

        if (!PasswordHasher.Verify(command.Code, challenge.CodeHash))
        {
            challenge.AttemptCount++;
            await db.SaveChangesAsync(ct);
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.TwoFactorInvalid, "Incorrect verification code.");
        }

        // Resolve the active scope for the new session from the user's primary membership.
        var scope = await scopeResolver.ResolvePrimaryAsync(challenge.UserId, ct);
        if (scope is null)
        {
            return ApiError.Problem(StatusCodes.Status403Forbidden,
                ErrorCodes.NoActiveCompany, "This user has no company membership to sign in to.");
        }

        // Single-device enforcement: identity is established (OTP verified, scope resolved) but access is
        // not yet granted. Only an ACTIVE binding counts as "already bound" — a Reset row must be treated
        // the same as no binding at all (re-registration), never as an occupied slot.
        var binding = await db.UserDeviceBindings
            .FirstOrDefaultAsync(b => b.UserId == challenge.UserId && b.Status == DeviceBindingStatus.Active, ct);

        if (binding is not null && binding.DeviceId != command.DeviceId)
        {
            return await RejectDeviceMismatchAsync(challenge, now, ct);
        }

        if (binding is not null)
        {
            // Same device re-authenticating. Flush this immediately (its own round trip) rather than
            // deferring to SessionIssuer's SaveChangesAsync below: UserDeviceBinding.RowVersion is a
            // concurrency token, so a concurrent write to this SAME row (a normal double-tap Verify, a
            // client retry-after-timeout, OR an admin reset landing in this exact window) can race us here
            // too and bump RowVersion first. On conflict we must NOT just shrug and proceed — an admin
            // reset racing in is a real "access should now be denied" outcome, not a cosmetic collision —
            // so we defer to the shared resolver rather than assuming our own stale read is still true.
            binding.LastAuthenticatedAtUtc = now.UtcDateTime;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(binding).State = EntityState.Detached;
                var rejection = await ResolveConcurrentWinnerAsync(challenge, command.DeviceId, now, ct);
                if (rejection is not null)
                {
                    return rejection;
                }
            }
        }
        else
        {
            // No ACTIVE binding: either this user has never registered a device, or their prior binding was
            // admin-reset. The unique index on UserId means there is at most one row per user regardless of
            // status, so a previously-reset row must be reused (flipped back to Active) rather than
            // duplicated with a fresh insert.
            //
            // BOTH sub-branches below are racy the same way: two concurrent verifies can read the identical
            // pre-write state (both see "no row", or both see the same Reset row) and then both attempt to
            // write. Neither branch trusts that read to still be true at write time — each is guarded
            // against a concurrent winner and resolves against whatever actually landed in the database.
            var existingRow = await db.UserDeviceBindings
                .FirstOrDefaultAsync(b => b.UserId == challenge.UserId, ct);

            if (existingRow is null)
            {
                var newBinding = new UserDeviceBinding
                {
                    Id = Guid.NewGuid(),
                    UserId = challenge.UserId,
                    DeviceId = command.DeviceId!,
                    Status = DeviceBindingStatus.Active,
                    RegisteredAtUtc = now.UtcDateTime,
                    LastAuthenticatedAtUtc = now.UtcDateTime
                };
                db.UserDeviceBindings.Add(newBinding);

                try
                {
                    // Flush the insert now, rather than batching it with the challenge/session writes
                    // below, so a concurrent first-registration race (two simultaneous verifies for the
                    // same brand-new user — a double-tap, or a client retry-after-timeout resending the
                    // same request) surfaces HERE as a catchable unique-index violation instead of an
                    // unhandled 500 out of SessionIssuer's SaveChangesAsync later.
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    // Lost the race: a concurrent request already inserted this user's binding between our
                    // read and our write. Drop our failed insert from the tracker — never leave a
                    // half-saved entity tracked — and defer to the shared resolver.
                    db.Entry(newBinding).State = EntityState.Detached;
                    var rejection = await ResolveConcurrentWinnerAsync(challenge, command.DeviceId, now, ct);
                    if (rejection is not null)
                    {
                        return rejection;
                    }
                }
                catch (DbUpdateException ex)
                {
                    // NOT the expected race — an unrelated failure (deadlock, connection drop, etc). Log it
                    // with full context and let it propagate as a genuine 500, rather than silently falling
                    // into the race-resolution logic above and misreporting an infra problem to the client
                    // as "invalid challenge" with nothing in the logs to tell the two apart.
                    logger.LogError(ex,
                        "Unexpected DbUpdateException registering a device binding for user {UserId} on challenge {ChallengeId}.",
                        challenge.UserId, challenge.Id);
                    throw;
                }
            }
            else
            {
                // Reuse a Reset row. This is an UPDATE, not an INSERT, so the unique index on UserId never
                // fires to catch a conflict. UserDeviceBinding.RowVersion is what closes that gap: without
                // it, a naive in-memory mutate-then-save-later would let TWO concurrent verifies against
                // the same just-reset row (the legit new device racing a client retry, or racing still-
                // valid credentials from the old device) both "win" and each issue a session for a
                // different device — defeating the entire point of the reset.
                existingRow.DeviceId = command.DeviceId!;
                existingRow.Status = DeviceBindingStatus.Active;
                existingRow.RegisteredAtUtc = now.UtcDateTime;
                existingRow.LastAuthenticatedAtUtc = now.UtcDateTime;
                // Clear the prior reset's audit fields — they describe the OLD (now-superseded)
                // registration, not this one.
                existingRow.ResetAtUtc = null;
                existingRow.ResetByUserId = null;

                try
                {
                    // Flush now, same reasoning as the insert branch: a concurrent re-registration racing
                    // this exact row must surface HERE, as a catchable optimistic-concurrency conflict on
                    // RowVersion, instead of silently overwriting (or being silently overwritten by) the
                    // other request inside SessionIssuer's SaveChangesAsync later.
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Lost the race: a concurrent request already updated this row (RowVersion moved)
                    // between our read and our write. Detach our stale copy — never leave a stale entity
                    // tracked — and defer to the shared resolver.
                    db.Entry(existingRow).State = EntityState.Detached;
                    var rejection = await ResolveConcurrentWinnerAsync(challenge, command.DeviceId, now, ct);
                    if (rejection is not null)
                    {
                        return rejection;
                    }
                }
                catch (DbUpdateException ex)
                {
                    // NOT the expected race — an unrelated failure (deadlock, connection drop, etc). Log it
                    // with full context and let it propagate as a genuine 500, rather than silently falling
                    // into the race-resolution logic above and misreporting an infra problem to the client
                    // as "invalid challenge" with nothing in the logs to tell the two apart.
                    logger.LogError(ex,
                        "Unexpected DbUpdateException reactivating a device binding for user {UserId} on challenge {ChallengeId}.",
                        challenge.UserId, challenge.Id);
                    throw;
                }
            }
        }

        // Success: consume the challenge and issue a session (SessionIssuer saves both in one transaction).
        challenge.ConsumedAtUtc = now.UtcDateTime;
        var user = challenge.User;
        var session = await sessionIssuer.IssueAsync(
            user, scope.Value.TenantId, scope.Value.CompanyId, scope.Value.LocationId, command.DeviceId, ct);

        return TypedResults.Ok(new AuthTokenResponse(
            session.AccessToken,
            "Bearer",
            session.AccessTokenExpiresAt,
            session.RefreshToken,
            session.RefreshTokenExpiresAt,
            UserSummary.From(user)));
    }
}
