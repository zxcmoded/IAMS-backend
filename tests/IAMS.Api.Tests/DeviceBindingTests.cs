using System.Security.Claims;
using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Features.Admin.ResetUserDeviceBinding;
using IAMS.Api.Features.Auth.Login;
using IAMS.Api.Features.Auth.VerifyTwoFactor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>Single-Device User Access / Device Binding: enforcement in VerifyTwoFactorHandler, the admin
/// reset slice, and the "SystemAdmin" authorization policy that gates it.</summary>
public class DeviceBindingTests
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

    private static async Task<(IamsDbContext Db, User User)> SeedUserAsync(string password = "correct horse")
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
        return (db, user);
    }

    private static VerifyTwoFactorHandler NewVerify(IamsDbContext db, FakeClock clock) =>
        new(db, new SessionIssuer(db, new JwtTokenService(Options), clock, Options), new ActiveScopeResolver(db), clock, Options,
            NullLogger<VerifyTwoFactorHandler>.Instance);

    private static async Task<(string ChallengeToken, string Otp)> LoginWithOtpAsync(IamsDbContext db, FakeClock clock)
    {
        var login = new LoginHandler(db, clock, Options, DevEnv, NullLogger<LoginHandler>.Instance);
        var ok = Assert.IsType<Ok<LoginResponse>>(
            (await login.HandleAsync(new LoginCommand("alice", "correct horse", null), CancellationToken.None)).Result);
        return (ok.Value!.ChallengeToken, ok.Value.DevOtp!);
    }

    [Fact]
    public async Task VerifyTwoFactor_FirstLogin_RegistersDeviceBinding()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (token, otp) = await LoginWithOtpAsync(db, clock);

        var result = await NewVerify(db, clock).HandleAsync(new VerifyTwoFactorCommand(token, otp, "device-1"), CancellationToken.None);

        Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(result.Result);
        var binding = Assert.Single(db.UserDeviceBindings);
        Assert.Equal("device-1", binding.DeviceId);
        Assert.Equal(DeviceBindingStatus.Active, binding.Status);
    }

    [Fact]
    public async Task VerifyTwoFactor_SameDevice_AllowsAccess_AndUpdatesLastAuthenticated_WithoutDuplicateRow()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (token1, otp1) = await LoginWithOtpAsync(db, clock);
        await NewVerify(db, clock).HandleAsync(new VerifyTwoFactorCommand(token1, otp1, "device-1"), CancellationToken.None);
        var firstSeen = db.UserDeviceBindings.Single().LastAuthenticatedAtUtc;

        clock.Advance(TimeSpan.FromMinutes(10));
        var (token2, otp2) = await LoginWithOtpAsync(db, clock);
        var result = await NewVerify(db, clock).HandleAsync(new VerifyTwoFactorCommand(token2, otp2, "device-1"), CancellationToken.None);

        Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(result.Result);
        var binding = Assert.Single(db.UserDeviceBindings); // still exactly one row, not recreated
        Assert.True(binding.LastAuthenticatedAtUtc > firstSeen);
        Assert.Equal(2, db.UserSessions.Count());
    }

    [Fact]
    public async Task VerifyTwoFactor_DifferentDevice_Returns403DeviceMismatch_AndConsumesChallenge()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (token1, otp1) = await LoginWithOtpAsync(db, clock);
        await NewVerify(db, clock).HandleAsync(new VerifyTwoFactorCommand(token1, otp1, "device-1"), CancellationToken.None);

        var (token2, otp2) = await LoginWithOtpAsync(db, clock);
        var result = await NewVerify(db, clock).HandleAsync(new VerifyTwoFactorCommand(token2, otp2, "device-2"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.DeviceMismatch, problem.ProblemDetails.Extensions["code"]);

        // Credentials + OTP were correct, so the challenge is consumed (not left valid for a retry).
        var secondChallenge = db.OtpChallenges.Single(c => c.ChallengeToken == token2);
        Assert.True(secondChallenge.IsConsumed);

        // No second session was issued, and the original binding is untouched.
        Assert.Single(db.UserSessions);
        Assert.Equal("device-1", db.UserDeviceBindings.Single().DeviceId);
    }

    [Fact]
    public async Task ResetUserDeviceBinding_NoActiveBinding_Returns404()
    {
        var (db, user) = await SeedUserAsync();
        var admin = new FakeCurrentUser { UserId = Guid.NewGuid(), IsSystemAdmin = true };
        var handler = new ResetUserDeviceBindingHandler(db, admin, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(new ResetUserDeviceBindingCommand(user.Id), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.NotFound, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ResetUserDeviceBinding_ThenReVerifyOnNewDevice_Succeeds_ReusingSameRow()
    {
        var (db, user) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (token1, otp1) = await LoginWithOtpAsync(db, clock);
        await NewVerify(db, clock).HandleAsync(new VerifyTwoFactorCommand(token1, otp1, "device-1"), CancellationToken.None);
        var originalRowId = db.UserDeviceBindings.Single().Id;

        var adminId = Guid.NewGuid();
        var admin = new FakeCurrentUser { UserId = adminId, IsSystemAdmin = true };
        var resetHandler = new ResetUserDeviceBindingHandler(db, admin, clock);
        var resetResult = await resetHandler.HandleAsync(new ResetUserDeviceBindingCommand(user.Id), CancellationToken.None);
        Assert.IsType<Ok<ResetUserDeviceBindingResponse>>(resetResult.Result);

        var resetBinding = db.UserDeviceBindings.Single();
        Assert.Equal(DeviceBindingStatus.Reset, resetBinding.Status);
        Assert.Equal(adminId, resetBinding.ResetByUserId);
        Assert.NotNull(resetBinding.ResetAtUtc);

        // A Reset binding must NOT be treated as "already bound" — the new device is allowed through.
        var (token2, otp2) = await LoginWithOtpAsync(db, clock);
        var verifyResult = await NewVerify(db, clock).HandleAsync(new VerifyTwoFactorCommand(token2, otp2, "device-2"), CancellationToken.None);

        Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(verifyResult.Result);
        var binding = Assert.Single(db.UserDeviceBindings); // reused the same row, unique index never violated
        Assert.Equal(originalRowId, binding.Id);
        Assert.Equal("device-2", binding.DeviceId);
        Assert.Equal(DeviceBindingStatus.Active, binding.Status);
        // The prior reset's audit fields describe the OLD (now-superseded) registration and must not
        // linger on the freshly re-activated binding.
        Assert.Null(binding.ResetAtUtc);
        Assert.Null(binding.ResetByUserId);
    }

    /// <summary>Seeds a bare, unconsumed OtpChallenge for <paramref name="userId"/> — enough for
    /// <c>ResolveConcurrentWinnerAsync</c>'s own needs (it only reads <c>UserId</c> and writes
    /// <c>ConsumedAtUtc</c>), without going through the full login flow.</summary>
    private static async Task<OtpChallenge> SeedChallengeAsync(IamsDbContext db, Guid userId)
    {
        var challenge = new OtpChallenge
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ChallengeToken = Guid.NewGuid().ToString("N"),
            CodeHash = PasswordHasher.Hash("000000"),
            Purpose = OtpPurpose.Login,
            Channel = TwoFactorChannel.Email,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            ResendAvailableAtUtc = DateTime.UtcNow
        };
        db.OtpChallenges.Add(challenge);
        await db.SaveChangesAsync();
        return challenge;
    }

    // ── ResolveConcurrentWinnerAsync direct coverage ────────────────────────────────────────────────
    // These call VerifyTwoFactorHandler's internal ResolveConcurrentWinnerAsync directly (see its own XML
    // doc for why) rather than trying to race two real handlers: the round-3 exploit this method exists to
    // close — "the concurrent winner turned out to be a revoked (Reset) binding" — can only be forced by a
    // genuine third-party write (an admin reset) landing in the handler's own read-then-write window, which
    // has no awaited yield point for a test to land in reliably. Racing it via Task.WhenAll was tried and
    // found by code review to pass ~93% of the time even with the bug reintroduced — worthless as a guard.
    // These tests instead pin the resolver's CONTRACT directly and deterministically; the companion test in
    // DeviceBindingSqlConcurrencyTests.cs proves the WIRING (that the match branch's catch really calls this
    // method on a genuine conflict) using a real, deterministically-forced two-context race.

    [Fact]
    public async Task ResolveConcurrentWinnerAsync_WinnerIsReset_RejectsStaleChallenge_AndConsumesIt()
    {
        var (db, user) = await SeedUserAsync();
        var challenge = await SeedChallengeAsync(db, user.Id);
        db.UserDeviceBindings.Add(new UserDeviceBinding
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = "device-1",
            Status = DeviceBindingStatus.Reset, // an admin reset "won" the race
            RegisteredAtUtc = DateTime.UtcNow.AddDays(-1),
            LastAuthenticatedAtUtc = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var handler = NewVerify(db, new FakeClock(DateTimeOffset.UtcNow));
        var rejection = await handler.ResolveConcurrentWinnerAsync(
            challenge, "device-1", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(rejection);
        var problem = rejection!;
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.TwoFactorInvalid, problem.ProblemDetails.Extensions["code"]);

        var reloaded = await db.OtpChallenges.AsNoTracking().SingleAsync(c => c.Id == challenge.Id);
        Assert.NotNull(reloaded.ConsumedAtUtc); // never leave a stale-but-still-valid challenge behind
    }

    [Fact]
    public async Task ResolveConcurrentWinnerAsync_WinnerIsNoLongerAnyBinding_RejectsStaleChallenge()
    {
        // Belt-and-braces: no row at all (not just Reset) must resolve the same way as Reset.
        var (db, user) = await SeedUserAsync();
        var challenge = await SeedChallengeAsync(db, user.Id);

        var handler = NewVerify(db, new FakeClock(DateTimeOffset.UtcNow));
        var rejection = await handler.ResolveConcurrentWinnerAsync(
            challenge, "device-1", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(rejection);
        var problem = rejection!;
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.TwoFactorInvalid, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ResolveConcurrentWinnerAsync_WinnerIsDifferentDevice_RejectsMismatch_AndConsumesChallenge()
    {
        var (db, user) = await SeedUserAsync();
        var challenge = await SeedChallengeAsync(db, user.Id);
        db.UserDeviceBindings.Add(new UserDeviceBinding
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = "winning-device",
            Status = DeviceBindingStatus.Active,
            RegisteredAtUtc = DateTime.UtcNow.AddDays(-1),
            LastAuthenticatedAtUtc = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var handler = NewVerify(db, new FakeClock(DateTimeOffset.UtcNow));
        var rejection = await handler.ResolveConcurrentWinnerAsync(
            challenge, "presented-device", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(rejection);
        var problem = rejection!;
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.DeviceMismatch, problem.ProblemDetails.Extensions["code"]);

        var reloaded = await db.OtpChallenges.AsNoTracking().SingleAsync(c => c.Id == challenge.Id);
        Assert.NotNull(reloaded.ConsumedAtUtc);
        // The winning binding itself must be untouched by the losing request.
        Assert.Equal("winning-device", db.UserDeviceBindings.Single().DeviceId);
    }

    [Fact]
    public async Task ResolveConcurrentWinnerAsync_WinnerIsSameDevice_ReturnsNull_AndBumpsLastAuthenticated()
    {
        var (db, user) = await SeedUserAsync();
        var challenge = await SeedChallengeAsync(db, user.Id);
        var originalSeen = DateTime.UtcNow.AddDays(-1);
        db.UserDeviceBindings.Add(new UserDeviceBinding
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = "device-1",
            Status = DeviceBindingStatus.Active,
            RegisteredAtUtc = DateTime.UtcNow.AddDays(-1),
            LastAuthenticatedAtUtc = originalSeen
        });
        await db.SaveChangesAsync();

        var handler = NewVerify(db, new FakeClock(DateTimeOffset.UtcNow));
        var now = DateTimeOffset.UtcNow;
        var rejection = await handler.ResolveConcurrentWinnerAsync(challenge, "device-1", now, CancellationToken.None);

        Assert.Null(rejection); // safe for the caller to proceed and issue a session
        // The resolver marks LastAuthenticatedAtUtc Modified but deliberately does NOT save it itself — the
        // real caller (HandleAsync) flushes it together with the session it goes on to issue. Simulate that
        // flush before reading back, exactly like the real success path does.
        await db.SaveChangesAsync();
        var binding = await db.UserDeviceBindings.AsNoTracking().SingleAsync();
        Assert.True(binding.LastAuthenticatedAtUtc > originalSeen);
        // A harmless same-device race must NOT consume the challenge itself — the caller (HandleAsync) is
        // the one that consumes it, on the success path that follows.
        var reloaded = await db.OtpChallenges.AsNoTracking().SingleAsync(c => c.Id == challenge.Id);
        Assert.Null(reloaded.ConsumedAtUtc);
    }

    [Fact]
    public void VerifyTwoFactorValidator_RejectsMissingDeviceId()
    {
        var validator = new VerifyTwoFactorValidator();

        var missing = validator.Validate(new VerifyTwoFactorCommand("tok", "123456", null));
        var empty = validator.Validate(new VerifyTwoFactorCommand("tok", "123456", ""));
        var present = validator.Validate(new VerifyTwoFactorCommand("tok", "123456", "device-1"));

        Assert.False(missing.IsValid);
        Assert.False(empty.IsValid);
        Assert.True(present.IsValid);
    }

    [Fact]
    public async Task SystemAdminPolicy_DeniesWithoutClaim_AllowsWithClaim()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
            options.AddPolicy("SystemAdmin", p => p.RequireClaim(IamsClaims.IsSystemAdmin, "true")));
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        var nonAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()) }, "Test"));
        var deniedResult = await authService.AuthorizeAsync(nonAdmin, "SystemAdmin");
        Assert.False(deniedResult.Succeeded);

        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(IamsClaims.IsSystemAdmin, "true") }, "Test"));
        var allowedResult = await authService.AuthorizeAsync(admin, "SystemAdmin");
        Assert.True(allowedResult.Succeeded);
    }
}
