using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Time;
using Microsoft.Extensions.Options;

namespace IAMS.Api.Common.Security;

public record IssuedSession(
    Guid SessionId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// Creates a <see cref="UserSession"/> (holding the active company/location scope + hashed refresh token)
/// and mints the matching access token. Shared by the activation and token-refresh slices.
///
/// The session stores ONLY the active company/location — never a snapshot of connection scope. Cross-tenant
/// connection policy is resolved live per request (see AccessCheckService / GetEffectiveScope), so a policy
/// change mid-session is honored without re-login (BR-TC-007).
/// </summary>
public class SessionIssuer(IamsDbContext db, JwtTokenService jwt, IClock clock, IOptions<JwtOptions> options)
{
    public async Task<IssuedSession> IssueAsync(
        User user, Guid tenantId, Guid activeCompanyId, Guid? activeLocationId, string? deviceId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rawRefresh = TokenGenerator.NewOpaqueToken();
        var refreshExpires = now.AddDays(options.Value.RefreshTokenDays);

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveCompanyId = activeCompanyId,
            ActiveLocationId = activeLocationId,
            SecurityStamp = user.SecurityStamp,
            DeviceId = deviceId,
            RefreshTokenHash = TokenGenerator.Sha256(rawRefresh),
            CreatedAtUtc = now.UtcDateTime,
            ExpiresAtUtc = refreshExpires.UtcDateTime,
            LastSeenAtUtc = now.UtcDateTime
        };
        db.UserSessions.Add(session);

        var scope = new AccessTokenScope(
            user.Id, user.Username, tenantId, activeCompanyId, activeLocationId, session.Id, user.IsSystemAdmin);
        var (accessToken, accessExpires) = jwt.CreateAccessToken(scope, now);

        await db.SaveChangesAsync(ct);

        return new IssuedSession(session.Id, accessToken, accessExpires, rawRefresh, refreshExpires);
    }
}
