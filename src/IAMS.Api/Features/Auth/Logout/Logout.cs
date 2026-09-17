using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Auth.Logout;

// ── Contract ────────────────────────────────────────────────────────────────
// Logout takes NO request body. The caller is identified by their bearer token, and the specific session
// to revoke is read from that token's session_id claim (see JwtTokenService.IamsClaims.SessionId) via
// ICurrentUser.SessionId.
//
// IMPORTANT: access tokens are stateless and permanent-per-device and are never re-validated against the
// DB per request, so this revocation is an AUDIT record only — it marks the UserSession row revoked but
// does NOT stop the already-issued token from continuing to authenticate. Clients should discard their
// stored token on logout; the server cannot force it to stop working.

// ── Handler ─────────────────────────────────────────────────────────────────
public class LogoutHandler(IamsDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<NoContent> HandleAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId;

        // Revoke only the caller's own session identified by the token's session_id claim, and only if it
        // isn't already revoked. Stay idempotent (always 204) — a missing/unknown/foreign session is a no-op.
        if (currentUser.SessionId is { } sessionId)
        {
            var session = await db.UserSessions
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);

            if (session is not null && session.RevokedAtUtc is null)
            {
                session.RevokedAtUtc = clock.UtcNow.UtcDateTime;
                await db.SaveChangesAsync(ct);
            }
        }

        return TypedResults.NoContent();
    }
}
