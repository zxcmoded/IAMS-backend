using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Time;

namespace IAMS.Api.Common.Security;

public record IssuedSession(
    Guid SessionId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt);

/// <summary>
/// Creates a <see cref="UserSession"/> (holding the active company/location scope) and mints the matching
/// access token. Shared by the activation slice; the <see cref="UserSession"/> row is retained purely for
/// audit/session bookkeeping (e.g. admin activation reset revokes it) — there is no refresh token, so no
/// opaque secret is generated or persisted.
///
/// The session stores ONLY the active company/location — never a snapshot of connection scope. Cross-tenant
/// connection policy is resolved live per request (see AccessCheckService / GetEffectiveScope), so a policy
/// change mid-session is honored without re-login (BR-TC-007).
/// </summary>
public class SessionIssuer(IamsDbContext db, JwtTokenService jwt, IClock clock)
{
    public async Task<IssuedSession> IssueAsync(
        User user, Guid tenantId, Guid activeCompanyId, Guid? activeLocationId, string? deviceId, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveCompanyId = activeCompanyId,
            ActiveLocationId = activeLocationId,
            SecurityStamp = user.SecurityStamp,
            DeviceId = deviceId,
            CreatedAtUtc = now.UtcDateTime,
            LastSeenAtUtc = now.UtcDateTime
        };

        var scope = new AccessTokenScope(
            user.Id, user.Username, tenantId, activeCompanyId, activeLocationId, session.Id, user.IsSystemAdmin);
        var (accessToken, accessExpires) = jwt.CreateAccessToken(scope, now);

        // The session's expiry mirrors the (now effectively permanent) access-token expiry — the token is
        // what actually authenticates; this column is retained only as session/audit metadata.
        session.ExpiresAtUtc = accessExpires.UtcDateTime;
        db.UserSessions.Add(session);

        await db.SaveChangesAsync(ct);

        return new IssuedSession(session.Id, accessToken, accessExpires);
    }
}
