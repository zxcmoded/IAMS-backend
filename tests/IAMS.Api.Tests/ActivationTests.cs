using System.Security.Claims;
using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using IAMS.Api.Features.Admin.ResetUserActivation;
using IAMS.Api.Features.Auth;
using IAMS.Api.Features.Auth.Activate;
using IAMS.Api.Features.Auth.RefreshToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Activation Key authentication: the single-credential activate flow (<see cref="ActivateHandler"/>),
/// its single-device enforcement, the admin reset slice, and the "SystemAdmin" authorization policy that
/// gates it. Replaces the old Login/2FA/UserDeviceBinding coverage now that the User table carries
/// activation/device-binding fields directly (see <see cref="User"/>'s doc comment for why the only race
/// shape left is a plain UPDATE-vs-UPDATE conflict on that row).
/// </summary>
public class ActivationTests
{
    private const string RawKey = "correct-horse-battery-staple-key";

    private static readonly IOptions<JwtOptions> Options = Microsoft.Extensions.Options.Options.Create(new JwtOptions
    {
        SigningKey = "test-signing-key-that-is-long-enough-32b",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30
    });

    private static async Task<(IamsDbContext Db, User User)> SeedUserAsync(string rawKey = RawKey, bool isActive = true)
    {
        var db = TestDb.New();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Kind = TenantKind.Parent };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "C" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            ActivationKeyHash = TokenGenerator.Sha256(rawKey),
            ActivationStatus = ActivationStatus.NotActivated,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = isActive
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

    private static ActivateHandler NewActivate(IamsDbContext db, FakeClock clock) =>
        new(db, new SessionIssuer(db, new JwtTokenService(Options), clock, Options), new ActiveScopeResolver(db), clock);

    [Fact]
    public async Task Activate_FreshKey_BindsDeviceAndIssuesSession()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var result = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var ok = Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(result.Result);
        Assert.False(string.IsNullOrEmpty(ok.Value!.AccessToken));
        Assert.False(string.IsNullOrEmpty(ok.Value.RefreshToken));

        var user = db.Users.Single();
        Assert.Equal(ActivationStatus.Activated, user.ActivationStatus);
        Assert.Equal("device-1", user.ActivatedDeviceId);
        Assert.NotNull(user.ActivatedAtUtc);
        Assert.Single(db.UserSessions);
    }

    [Fact]
    public async Task Activate_SameDeviceReAuthenticating_Succeeds_WithoutChangingBinding()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);
        var boundAt = db.Users.Single().ActivatedAtUtc;

        clock.Advance(TimeSpan.FromMinutes(10));
        var result = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(result.Result);
        var user = db.Users.Single();
        Assert.Equal(ActivationStatus.Activated, user.ActivationStatus);
        Assert.Equal("device-1", user.ActivatedDeviceId);
        Assert.Equal(boundAt, user.ActivatedAtUtc); // unchanged — re-auth does not re-bind
        Assert.Equal(2, db.UserSessions.Count());
    }

    [Fact]
    public async Task Activate_DifferentDevice_Returns403AlreadyBound_AndLeavesBindingUntouched()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var result = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-2"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.ActivationKeyAlreadyBound, problem.ProblemDetails.Extensions["code"]);

        Assert.Single(db.UserSessions); // no second session issued
        Assert.Equal("device-1", db.Users.Single().ActivatedDeviceId); // untouched
    }

    [Fact]
    public async Task Activate_UnknownKey_Returns401Invalid()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var result = await NewActivate(db, clock).HandleAsync(new ActivateCommand("not-the-right-key", "device-1"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.ActivationKeyInvalid, problem.ProblemDetails.Extensions["code"]);
        Assert.Empty(db.UserSessions);
    }

    [Fact]
    public async Task Activate_InactiveUser_Returns401Invalid_SameCodeAsUnknownKey()
    {
        // Deliberately the SAME code as an unknown key — an unauthenticated caller must not be able to
        // tell "no such key" apart from "key belongs to a deactivated account" (see ErrorCodes.ActivationKeyInvalid).
        var (db, _) = await SeedUserAsync(isActive: false);
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var result = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.ActivationKeyInvalid, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task Activate_NoCompanyMembership_Returns403NoActiveCompany()
    {
        var db = TestDb.New();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "orphan",
            ActivationKeyHash = TokenGenerator.Sha256(RawKey),
            ActivationStatus = ActivationStatus.NotActivated,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var result = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.NoActiveCompany, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public void ActivateValidator_RejectsMissingOrTooShortDeviceIdAndKey()
    {
        var validator = new ActivateValidator();

        Assert.False(validator.Validate(new ActivateCommand("", "device-1")).IsValid);
        Assert.False(validator.Validate(new ActivateCommand("short", "device-1")).IsValid); // < 8 chars
        Assert.False(validator.Validate(new ActivateCommand(RawKey, "")).IsValid);
        Assert.False(validator.Validate(new ActivateCommand(RawKey, "sh")).IsValid); // < 8 chars
        Assert.False(validator.Validate(new ActivateCommand(RawKey, "has a space")).IsValid); // bad shape
        Assert.True(validator.Validate(new ActivateCommand(RawKey, "device-1")).IsValid);
    }

    // ── Admin reset ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetUserActivation_NoActiveActivation_Returns404()
    {
        var (db, user) = await SeedUserAsync();
        var admin = new FakeCurrentUser { UserId = Guid.NewGuid(), IsSystemAdmin = true };
        var handler = new ResetUserActivationHandler(db, admin, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(new ResetUserActivationCommand(user.Id), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.NotFound, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ResetUserActivation_UnknownUser_Returns404()
    {
        var db = TestDb.New();
        var admin = new FakeCurrentUser { UserId = Guid.NewGuid(), IsSystemAdmin = true };
        var handler = new ResetUserActivationHandler(db, admin, new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(new ResetUserActivationCommand(Guid.NewGuid()), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    [Fact]
    public async Task ResetUserActivation_ZeroGuid_Returns404_NotValidationError()
    {
        // QA/code-review-flagged: the literal all-zero GUID is syntactically valid (just never a real
        // user), so it must 404 exactly like any other non-existent id — not 400 validation_failed.
        var db = TestDb.New();
        var admin = new FakeCurrentUser { UserId = Guid.NewGuid(), IsSystemAdmin = true };
        var handler = new ResetUserActivationHandler(db, admin, new FakeClock(DateTimeOffset.UtcNow));

        var command = new ResetUserActivationCommand(Guid.Empty);
        var validation = new ResetUserActivationValidator().Validate(command);
        Assert.True(validation.IsValid);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.NotFound, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ResetUserActivation_RevokesExistingSession_SoItCanNoLongerRefresh()
    {
        // CRITICAL fix from code review: a session issued to the OLD (now-reset) device must not keep
        // silently renewing itself via refresh for up to RefreshTokenDays after an admin reset.
        var (db, user) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var originalStamp = user.SecurityStamp;
        var activateResult = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);
        var issuedRefreshToken = Assert.IsType<Ok<AuthTokenResponse>>(activateResult.Result).Value!.RefreshToken;

        var admin = new FakeCurrentUser { UserId = Guid.NewGuid(), IsSystemAdmin = true };
        var resetHandler = new ResetUserActivationHandler(db, admin, clock);
        var resetResult = await resetHandler.HandleAsync(new ResetUserActivationCommand(user.Id), CancellationToken.None);
        Assert.IsType<Ok<ResetUserActivationResponse>>(resetResult.Result);

        // Both revocation mechanisms should now be in place.
        var resetUser = db.Users.Single();
        Assert.NotEqual(originalStamp, resetUser.SecurityStamp);
        Assert.Equal(1, db.UserSessions.Count(s => s.RevokedAtUtc != null));

        var refreshHandler = new RefreshTokenHandler(
            db, new SessionIssuer(db, new JwtTokenService(Options), clock, Options), new ActiveScopeResolver(db), clock);
        var refreshResult = await refreshHandler.HandleAsync(
            new RefreshTokenCommand(issuedRefreshToken, "device-1"), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(refreshResult.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.SessionExpired, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ResetUserActivation_ThenReactivateOnNewDevice_Succeeds_PreservingThenClearingAudit()
    {
        var (db, user) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var adminId = Guid.NewGuid();
        var admin = new FakeCurrentUser { UserId = adminId, IsSystemAdmin = true };
        var resetHandler = new ResetUserActivationHandler(db, admin, clock);
        var resetResult = await resetHandler.HandleAsync(new ResetUserActivationCommand(user.Id), CancellationToken.None);
        Assert.IsType<Ok<ResetUserActivationResponse>>(resetResult.Result);

        var resetUser = db.Users.Single();
        Assert.Equal(ActivationStatus.NotActivated, resetUser.ActivationStatus);
        Assert.Equal(adminId, resetUser.ActivationResetByUserId);
        Assert.NotNull(resetUser.ActivationResetAtUtc);
        // Audit trail of the OLD registration is preserved until the next activation overwrites it.
        Assert.Equal("device-1", resetUser.ActivatedDeviceId);

        // A reset user must NOT be treated as "already bound" — the new device is allowed through.
        var verifyResult = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-2"), CancellationToken.None);

        Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(verifyResult.Result);
        var reactivated = db.Users.Single();
        Assert.Equal(user.Id, reactivated.Id); // same row, never duplicated
        Assert.Equal("device-2", reactivated.ActivatedDeviceId);
        Assert.Equal(ActivationStatus.Activated, reactivated.ActivationStatus);
        // The prior reset's audit fields describe the OLD (now-superseded) registration and must not
        // linger on the freshly re-activated binding.
        Assert.Null(reactivated.ActivationResetAtUtc);
        Assert.Null(reactivated.ActivationResetByUserId);
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
