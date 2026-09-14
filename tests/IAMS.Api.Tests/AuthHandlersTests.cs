using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Features.Auth;
using IAMS.Api.Features.Auth.RefreshToken;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Token refresh: orthogonal to how the session was first issued (Activation Key now, previously
/// username/password + 2FA), so this coverage carries over unchanged in shape — only the seeding helper
/// changed, from a password hash to an Activation Key hash. See <c>ActivationTests</c> for the
/// activation/device-binding state machine itself.
/// </summary>
public class AuthHandlersTests
{
    private static readonly IOptions<JwtOptions> Options = Microsoft.Extensions.Options.Options.Create(new JwtOptions
    {
        SigningKey = "test-signing-key-that-is-long-enough-32b",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30
    });

    private static async Task<(IamsDbContext Db, User User, Guid TenantId, Guid CompanyId)> SeedUserAsync()
    {
        var db = TestDb.New();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Kind = TenantKind.Parent };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "C" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            ActivationKeyHash = TokenGenerator.Sha256("a-fresh-activation-key"),
            ActivationStatus = ActivationStatus.NotActivated,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = true
        };
        var membership = new UserCompanyMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CompanyId = company.Id,
            IsPrimary = true
        };
        db.AddRange(tenant, company, user, membership);
        await db.SaveChangesAsync();
        return (db, user, tenant.Id, company.Id);
    }

    private static SessionIssuer NewSessionIssuer(IamsDbContext db, FakeClock clock) =>
        new(db, new JwtTokenService(Options), clock, Options);

    [Fact]
    public async Task Refresh_WithRevokedSession_ReturnsSessionExpired()
    {
        var (db, user, tenantId, companyId) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var issuer = NewSessionIssuer(db, clock);
        var issued = await issuer.IssueAsync(user, tenantId, companyId, null, "device-1", CancellationToken.None);

        var session = db.UserSessions.Single();
        session.RevokedAtUtc = clock.UtcNow.UtcDateTime;
        await db.SaveChangesAsync();

        var refresh = new RefreshTokenHandler(db, issuer, new ActiveScopeResolver(db), clock);
        var result = await refresh.HandleAsync(new RefreshTokenCommand(issued.RefreshToken, "device-1"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
    }

    [Fact]
    public async Task Refresh_WhenUserDeactivated_ReturnsSessionExpired()
    {
        var (db, user, tenantId, companyId) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var issuer = NewSessionIssuer(db, clock);
        var issued = await issuer.IssueAsync(user, tenantId, companyId, null, "device-1", CancellationToken.None);

        // Admin deactivates the user after the session was issued.
        (await db.Users.FindAsync(user.Id))!.IsActive = false;
        await db.SaveChangesAsync();

        var refresh = new RefreshTokenHandler(db, issuer, new ActiveScopeResolver(db), clock);
        var result = await refresh.HandleAsync(new RefreshTokenCommand(issued.RefreshToken, "device-1"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
    }

    [Fact]
    public async Task Refresh_AfterSecurityStampRotation_ReturnsSessionExpired()
    {
        var (db, user, tenantId, companyId) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var issuer = NewSessionIssuer(db, clock);
        var issued = await issuer.IssueAsync(user, tenantId, companyId, null, "device-1", CancellationToken.None);

        // Rotating the SecurityStamp (e.g. forced logout / activation reset) invalidates the session.
        (await db.Users.FindAsync(user.Id))!.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();

        var refresh = new RefreshTokenHandler(db, issuer, new ActiveScopeResolver(db), clock);
        var result = await refresh.HandleAsync(new RefreshTokenCommand(issued.RefreshToken, "device-1"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithValidSession_RotatesToken()
    {
        var (db, user, tenantId, companyId) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var issuer = NewSessionIssuer(db, clock);
        var issued = await issuer.IssueAsync(user, tenantId, companyId, null, "device-1", CancellationToken.None);

        var refresh = new RefreshTokenHandler(db, issuer, new ActiveScopeResolver(db), clock);
        var result = await refresh.HandleAsync(new RefreshTokenCommand(issued.RefreshToken, "device-1"), CancellationToken.None);

        var ok = Assert.IsType<Ok<AuthTokenResponse>>(result.Result);
        Assert.NotEqual(issued.RefreshToken, ok.Value!.RefreshToken);
        Assert.Equal(2, db.UserSessions.Count());
        Assert.Contains(db.UserSessions, s => s.RevokedAtUtc != null);
    }
}
