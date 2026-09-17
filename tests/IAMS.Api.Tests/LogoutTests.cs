using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.Auth.Logout;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Logout now takes no request body: the session to revoke is read from the caller's own bearer token
/// (session_id claim, surfaced via <see cref="Common.Security.ICurrentUser.SessionId"/>). Revocation is an
/// audit record only — it does not (and cannot) invalidate the stateless, permanent access token itself.
/// </summary>
public class LogoutTests
{
    private static async Task<(IamsDbContext Db, User User, UserSession Session)> SeedSessionAsync()
    {
        var db = TestDb.New();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            ActivationKeyHash = "h",
            ActivationStatus = ActivationStatus.Activated,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = true
        };
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveCompanyId = Guid.NewGuid(),
            SecurityStamp = user.SecurityStamp,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddYears(100),
            LastSeenAtUtc = DateTime.UtcNow
        };
        db.AddRange(user, session);
        await db.SaveChangesAsync();
        return (db, user, session);
    }

    [Fact]
    public async Task Logout_RevokesCallersOwnSession()
    {
        var (db, user, session) = await SeedSessionAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var currentUser = new FakeCurrentUser { UserId = user.Id, SessionId = session.Id };
        var handler = new LogoutHandler(db, currentUser, clock);

        var result = await handler.HandleAsync(CancellationToken.None);

        Assert.IsType<NoContent>(result);
        Assert.Equal(clock.UtcNow.UtcDateTime, db.UserSessions.Single().RevokedAtUtc);
    }

    [Fact]
    public async Task Logout_WithNoSessionClaim_IsNoOp_ButReturnsNoContent()
    {
        var (db, user, _) = await SeedSessionAsync();
        var currentUser = new FakeCurrentUser { UserId = user.Id, SessionId = null };
        var handler = new LogoutHandler(db, currentUser, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(CancellationToken.None);

        Assert.IsType<NoContent>(result);
        Assert.Null(db.UserSessions.Single().RevokedAtUtc);
    }

    [Fact]
    public async Task Logout_SessionBelongingToAnotherUser_IsNotRevoked()
    {
        var (db, _, session) = await SeedSessionAsync();
        var currentUser = new FakeCurrentUser { UserId = Guid.NewGuid(), SessionId = session.Id };
        var handler = new LogoutHandler(db, currentUser, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(CancellationToken.None);

        Assert.IsType<NoContent>(result);
        Assert.Null(db.UserSessions.Single().RevokedAtUtc);
    }

    [Fact]
    public async Task Logout_AlreadyRevoked_StaysIdempotent_KeepingOriginalTimestamp()
    {
        var (db, user, session) = await SeedSessionAsync();
        var firstRevoke = DateTime.UtcNow.AddMinutes(-5);
        session.RevokedAtUtc = firstRevoke;
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUser { UserId = user.Id, SessionId = session.Id };
        var handler = new LogoutHandler(db, currentUser, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(CancellationToken.None);

        Assert.IsType<NoContent>(result);
        Assert.Equal(firstRevoke, db.UserSessions.Single().RevokedAtUtc);
    }
}
