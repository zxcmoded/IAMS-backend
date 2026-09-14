using FluentValidation;
using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Auth.RefreshToken;

// ── Contract ────────────────────────────────────────────────────────────────
public record RefreshTokenCommand(string RefreshToken, string? DeviceId);

public class RefreshTokenValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
        RuleFor(x => x.DeviceId).MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class RefreshTokenHandler(
    IamsDbContext db,
    SessionIssuer sessionIssuer,
    ActiveScopeResolver scopeResolver,
    IClock clock)
{
    public async Task<Results<Ok<AuthTokenResponse>, ProblemHttpResult>> HandleAsync(
        RefreshTokenCommand command, CancellationToken ct)
    {
        var hash = TokenGenerator.Sha256(command.RefreshToken);
        var now = clock.UtcNow;

        var session = await db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);

        // A missing, revoked, or expired session surfaces as the F1 "Session Expired" screen.
        if (session is null || !session.IsActive(now.UtcDateTime))
        {
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.SessionExpired, "Your session has expired. Please sign in again.");
        }

        // Consistent with login: a deactivated user cannot refresh. And a rotated SecurityStamp (password
        // change / forced revocation) invalidates every outstanding session, since the stamp snapshotted
        // at issue time no longer matches the user's current stamp.
        if (!session.User.IsActive || session.SecurityStamp != session.User.SecurityStamp)
        {
            session.RevokedAtUtc = now.UtcDateTime;
            await db.SaveChangesAsync(ct);
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.SessionExpired, "Your session is no longer valid. Please sign in again.");
        }

        // The session already carries the active company/location; derive the tenant live.
        var tenantId = await scopeResolver.ResolveTenantForCompanyAsync(session.ActiveCompanyId, ct);
        if (tenantId is null)
        {
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.SessionExpired, "Your session is no longer valid. Please sign in again.");
        }

        // Rotate: revoke the presented session, then issue a fresh one carrying the same active scope.
        session.RevokedAtUtc = now.UtcDateTime;
        var user = session.User;
        var issued = await sessionIssuer.IssueAsync(
            user, tenantId.Value, session.ActiveCompanyId, session.ActiveLocationId,
            command.DeviceId ?? session.DeviceId, ct);

        return TypedResults.Ok(new AuthTokenResponse(
            issued.AccessToken,
            "Bearer",
            issued.AccessTokenExpiresAt,
            issued.RefreshToken,
            issued.RefreshTokenExpiresAt,
            UserSummary.From(user)));
    }
}
