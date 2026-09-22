using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Time;

namespace IAMS.Api.Common.Security;

public record IssuedSession(
    Guid SessionId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt);

/// <summary>
/// Creates a <see cref="UserSession"/> and mints the matching access token. The <see cref="UserSession"/>
/// row is retained purely for audit/session bookkeeping (e.g. admin activation reset revokes it) — there is
/// no refresh token, so no opaque secret is generated or persisted. The token carries the user's identity,
/// their single Company, and their role; data-access scope (Company + assigned Locations) is resolved live
/// per request, never frozen here.
/// </summary>
public class SessionIssuer(IamsDbContext db, JwtTokenService jwt, IClock clock)
{
    public async Task<IssuedSession> IssueAsync(User user, string? deviceId, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp,
            DeviceId = deviceId,
            CreatedAtUtc = now.UtcDateTime,
            LastSeenAtUtc = now.UtcDateTime
        };

        var scope = new AccessTokenScope(user.Id, user.Username, user.CompanyId, user.Role, session.Id);
        var (accessToken, accessExpires) = jwt.CreateAccessToken(scope, now);

        // The session's expiry mirrors the (now effectively permanent) access-token expiry — the token is
        // what actually authenticates; this column is retained only as session/audit metadata.
        session.ExpiresAtUtc = accessExpires.UtcDateTime;
        db.UserSessions.Add(session);

        await db.SaveChangesAsync(ct);

        return new IssuedSession(session.Id, accessToken, accessExpires);
    }
}
