using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Features.Auth;
using IAMS.Api.Features.Auth.Login;
using IAMS.Api.Features.Auth.RefreshToken;
using IAMS.Api.Features.Auth.ResendTwoFactor;
using IAMS.Api.Features.Auth.VerifyTwoFactor;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAMS.Api.Tests;

public class AuthHandlersTests
{
    private static readonly IOptions<JwtOptions> Options = Microsoft.Extensions.Options.Options.Create(new JwtOptions
    {
        SigningKey = "test-signing-key-that-is-long-enough-32b",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
        TwoFactorCodeTtlSeconds = 300,
        TwoFactorResendCooldownSeconds = 30,
        TwoFactorMaxAttempts = 5
    });

    private static readonly FakeHostEnvironment DevEnv = new() { EnvironmentName = "Development" };

    private static async Task<(IamsDbContext Db, User User, Guid TenantId, Guid CompanyId)> SeedUserAsync(string password = "correct horse")
    {
        var db = TestDb.New();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Kind = TenantKind.Parent };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "C" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            NormalizedUsername = "ALICE",
            PasswordHash = PasswordHasher.Hash(password),
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

    private static VerifyTwoFactorHandler NewVerify(IamsDbContext db, FakeClock clock) =>
        new(db, NewSessionIssuer(db, clock), new ActiveScopeResolver(db), clock, Options,
            NullLogger<VerifyTwoFactorHandler>.Instance);

    [Fact]
    public async Task Login_WithValidCredentials_StartsTwoFactorChallenge()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var handler = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);

        var result = await handler.HandleAsync(new LoginCommand("alice", "correct horse", "device-1"), CancellationToken.None);

        var ok = Assert.IsType<Ok<LoginResponse>>(result.Result);
        Assert.False(string.IsNullOrEmpty(ok.Value!.ChallengeToken));
        Assert.NotNull(ok.Value.DevOtp);
        Assert.Single(db.OtpChallenges);
    }

    [Fact]
    public async Task Login_IsCaseInsensitiveOnUsername()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var handler = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);

        var result = await handler.HandleAsync(new LoginCommand("ALICE", "correct horse", null), CancellationToken.None);

        Assert.IsType<Ok<LoginResponse>>(result.Result);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var handler = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);

        var result = await handler.HandleAsync(new LoginCommand("alice", "wrong", null), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
    }

    [Fact]
    public async Task VerifyTwoFactor_WithCorrectCode_IssuesSession()
    {
        var (db, _, _, companyId) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var login = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);
        var loginOk = Assert.IsType<Ok<LoginResponse>>(
            (await login.HandleAsync(new LoginCommand("alice", "correct horse", null), CancellationToken.None)).Result);

        var result = await NewVerify(db, clock).HandleAsync(
            new VerifyTwoFactorCommand(loginOk.Value!.ChallengeToken, loginOk.Value.DevOtp!, "device-1"), CancellationToken.None);

        var ok = Assert.IsType<Ok<AuthTokenResponse>>(result.Result);
        Assert.False(string.IsNullOrEmpty(ok.Value!.AccessToken));
        Assert.False(string.IsNullOrEmpty(ok.Value.RefreshToken));
        var session = Assert.Single(db.UserSessions);
        Assert.Equal(companyId, session.ActiveCompanyId);
        Assert.True(session.IsTwoFactorComplete);
        Assert.True(db.OtpChallenges.Single().IsConsumed);
    }

    [Fact]
    public async Task VerifyTwoFactor_WithWrongCode_IncrementsAttemptsAndFails()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var login = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);
        var loginOk = Assert.IsType<Ok<LoginResponse>>(
            (await login.HandleAsync(new LoginCommand("alice", "correct horse", null), CancellationToken.None)).Result);

        var result = await NewVerify(db, clock).HandleAsync(
            new VerifyTwoFactorCommand(loginOk.Value!.ChallengeToken, "000000", null), CancellationToken.None);

        Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(1, db.OtpChallenges.Single().AttemptCount);
        Assert.Empty(db.UserSessions);
    }

    [Fact]
    public async Task VerifyTwoFactor_AfterExpiry_Returns401()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var login = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);
        var loginOk = Assert.IsType<Ok<LoginResponse>>(
            (await login.HandleAsync(new LoginCommand("alice", "correct horse", null), CancellationToken.None)).Result);
        var otp = loginOk.Value!.DevOtp!;

        clock.Advance(TimeSpan.FromSeconds(Options.Value.TwoFactorCodeTtlSeconds + 1));

        var result = await NewVerify(db, clock).HandleAsync(
            new VerifyTwoFactorCommand(loginOk.Value.ChallengeToken, otp, null), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
    }

    [Fact]
    public async Task Resend_BeforeCooldown_Returns429()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var login = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);
        var loginOk = Assert.IsType<Ok<LoginResponse>>(
            (await login.HandleAsync(new LoginCommand("alice", "correct horse", null), CancellationToken.None)).Result);

        var resend = new ResendTwoFactorHandler(db, clock, Options, DevEnv, NullLogger<ResendTwoFactorHandler>.Instance);
        var result = await resend.HandleAsync(new ResendTwoFactorCommand(loginOk.Value!.ChallengeToken), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.StatusCode);
    }

    [Fact]
    public async Task Resend_AfterCooldown_RotatesCode()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var login = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);
        var loginOk = Assert.IsType<Ok<LoginResponse>>(
            (await login.HandleAsync(new LoginCommand("alice", "correct horse", null), CancellationToken.None)).Result);
        var firstOtp = loginOk.Value!.DevOtp!;

        clock.Advance(TimeSpan.FromSeconds(Options.Value.TwoFactorResendCooldownSeconds + 1));

        var resend = new ResendTwoFactorHandler(db, clock, Options, DevEnv, NullLogger<ResendTwoFactorHandler>.Instance);
        var result = await resend.HandleAsync(new ResendTwoFactorCommand(loginOk.Value.ChallengeToken), CancellationToken.None);

        var ok = Assert.IsType<Ok<ResendTwoFactorResponse>>(result.Result);
        Assert.NotNull(ok.Value!.DevOtp);
        // Old code no longer verifies; new code does.
        var challenge = db.OtpChallenges.Single();
        Assert.True(PasswordHasher.Verify(ok.Value.DevOtp!, challenge.CodeHash));
        Assert.Equal(1, challenge.ResendCount);
    }

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
    public async Task Login_WithUnknownUsername_Returns401()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var handler = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);

        // Unknown user still returns the uniform 401 (and internally runs the dummy PBKDF2 verify).
        var result = await handler.HandleAsync(new LoginCommand("nobody", "whatever", null), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
    }

    [Fact]
    public async Task Resend_BeyondTotalCap_IsRejected()
    {
        var (db, _, _, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var login = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);
        var loginOk = Assert.IsType<Ok<LoginResponse>>(
            (await login.HandleAsync(new LoginCommand("alice", "correct horse", null), CancellationToken.None)).Result);
        var token = loginOk.Value!.ChallengeToken;
        var resend = new ResendTwoFactorHandler(db, clock, Options, DevEnv, NullLogger<ResendTwoFactorHandler>.Instance);

        // Exhaust the allowed resends (each after its cooldown).
        for (var i = 0; i < Options.Value.TwoFactorMaxResends; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(Options.Value.TwoFactorResendCooldownSeconds + 1));
            var ok = await resend.HandleAsync(new ResendTwoFactorCommand(token), CancellationToken.None);
            Assert.IsType<Ok<ResendTwoFactorResponse>>(ok.Result);
        }

        // One more, past cooldown, is rejected by the total cap.
        clock.Advance(TimeSpan.FromSeconds(Options.Value.TwoFactorResendCooldownSeconds + 1));
        var capped = await resend.HandleAsync(new ResendTwoFactorCommand(token), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(capped.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.StatusCode);
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

        // Rotating the SecurityStamp (e.g. password change / forced logout) invalidates the session.
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
