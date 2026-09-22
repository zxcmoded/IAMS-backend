using System.Security.Claims;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Features.Admin.ResetUserActivation;
using IAMS.Api.Features.Auth;
using IAMS.Api.Features.Auth.Activate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Activation Key authentication: the single-credential activate flow (<see cref="ActivateHandler"/>),
/// its single-device enforcement, the admin reset slice, and the role-based authorization policy that
/// gates it. Every user now belongs to exactly one Company (non-nullable FK) and holds one
/// <see cref="UserRole"/>, so there is no "no active company" branch to guard.
/// </summary>
public class ActivationTests
{
    private const string RawKey = "correct-horse-battery-staple-key";

    private static readonly IOptions<JwtOptions> Options = Microsoft.Extensions.Options.Options.Create(new JwtOptions
    {
        SigningKey = "test-signing-key-that-is-long-enough-32b",
        AccessTokenLifetimeDays = 36500
    });

    private static async Task<(IamsDbContext Db, User User)> SeedUserAsync(string rawKey = RawKey, bool isActive = true)
    {
        var db = TestDb.New();
        var company = new Company { Id = Guid.NewGuid(), Name = "C" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            CompanyId = company.Id,
            Role = UserRole.User,
            ActivationKeyHash = TokenGenerator.Sha256(rawKey),
            ActivationStatus = ActivationStatus.NotActivated,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = isActive
        };
        db.AddRange(company, user);
        await db.SaveChangesAsync();
        return (db, user);
    }

    private static ActivateHandler NewActivate(IamsDbContext db, FakeClock clock) =>
        new(db, new SessionIssuer(db, new JwtTokenService(Options), clock), clock);

    [Fact]
    public async Task Activate_FreshKey_BindsDeviceAndIssuesSession()
    {
        var (db, _) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var result = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var ok = Assert.IsType<Ok<AuthTokenResponse>>(result.Result);
        Assert.False(string.IsNullOrEmpty(ok.Value!.AccessToken));
        // The auth response now carries the user's Company + role for the client.
        Assert.Equal(200, ok.Value.User.Role.Code);
        Assert.Equal("User", ok.Value.User.Role.Name);

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

        Assert.IsType<Ok<AuthTokenResponse>>(result.Result);
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

    private static FakeCurrentUser SuperAdmin() =>
        new() { UserId = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Role = UserRole.SuperAdmin };

    [Fact]
    public async Task ResetUserActivation_NoActiveActivation_Returns404()
    {
        var (db, user) = await SeedUserAsync();
        var handler = new ResetUserActivationHandler(db, SuperAdmin(), new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(new ResetUserActivationCommand(user.Id), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.NotFound, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ResetUserActivation_UnknownUser_Returns404()
    {
        var db = TestDb.New();
        var handler = new ResetUserActivationHandler(db, SuperAdmin(), new FakeClock(DateTimeOffset.UtcNow));

        var result = await handler.HandleAsync(new ResetUserActivationCommand(Guid.NewGuid()), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    [Fact]
    public async Task ResetUserActivation_AdminFromDifferentCompany_Returns404()
    {
        // An Admin may only reset users in their own Company; a target elsewhere is "not found" to them.
        var (db, user) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var foreignAdmin = new FakeCurrentUser { UserId = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Role = UserRole.Admin };
        var handler = new ResetUserActivationHandler(db, foreignAdmin, clock);
        var result = await handler.HandleAsync(new ResetUserActivationCommand(user.Id), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    [Fact]
    public async Task ResetUserActivation_ZeroGuid_Returns404_NotValidationError()
    {
        var db = TestDb.New();
        var handler = new ResetUserActivationHandler(db, SuperAdmin(), new FakeClock(DateTimeOffset.UtcNow));

        var command = new ResetUserActivationCommand(Guid.Empty);
        var validation = new ResetUserActivationValidator().Validate(command);
        Assert.True(validation.IsValid);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(IAMS.Api.Common.Errors.ErrorCodes.NotFound, problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ResetUserActivation_RevokesExistingSessionForAudit_AndRotatesSecurityStamp()
    {
        var (db, user) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var originalStamp = user.SecurityStamp;
        await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var resetHandler = new ResetUserActivationHandler(db, SuperAdmin(), clock);
        var resetResult = await resetHandler.HandleAsync(new ResetUserActivationCommand(user.Id), CancellationToken.None);
        Assert.IsType<Ok<ResetUserActivationResponse>>(resetResult.Result);

        var resetUser = db.Users.Single();
        Assert.NotEqual(originalStamp, resetUser.SecurityStamp);
        Assert.Equal(ActivationStatus.NotActivated, resetUser.ActivationStatus);
        Assert.Equal(1, db.UserSessions.Count(s => s.RevokedAtUtc != null));
    }

    [Fact]
    public async Task ResetUserActivation_ThenReactivateOnNewDevice_Succeeds_PreservingThenClearingAudit()
    {
        var (db, user) = await SeedUserAsync();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-1"), CancellationToken.None);

        var adminId = Guid.NewGuid();
        var admin = new FakeCurrentUser { UserId = adminId, CompanyId = Guid.NewGuid(), Role = UserRole.SuperAdmin };
        var resetHandler = new ResetUserActivationHandler(db, admin, clock);
        var resetResult = await resetHandler.HandleAsync(new ResetUserActivationCommand(user.Id), CancellationToken.None);
        Assert.IsType<Ok<ResetUserActivationResponse>>(resetResult.Result);

        var resetUser = db.Users.Single();
        Assert.Equal(ActivationStatus.NotActivated, resetUser.ActivationStatus);
        Assert.Equal(adminId, resetUser.ActivationResetByUserId);
        Assert.NotNull(resetUser.ActivationResetAtUtc);
        Assert.Equal("device-1", resetUser.ActivatedDeviceId);

        var verifyResult = await NewActivate(db, clock).HandleAsync(new ActivateCommand(RawKey, "device-2"), CancellationToken.None);

        Assert.IsType<Ok<AuthTokenResponse>>(verifyResult.Result);
        var reactivated = db.Users.Single();
        Assert.Equal(user.Id, reactivated.Id); // same row, never duplicated
        Assert.Equal("device-2", reactivated.ActivatedDeviceId);
        Assert.Equal(ActivationStatus.Activated, reactivated.ActivationStatus);
        Assert.Null(reactivated.ActivationResetAtUtc);
        Assert.Null(reactivated.ActivationResetByUserId);
    }

    [Fact]
    public async Task ManageCompanyPolicy_DeniesBelowAdmin_AllowsAdminAndAbove()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options => options.AddIamsAuthorization());
        var provider = services.BuildServiceProvider();
        var authService = provider.GetRequiredService<IAuthorizationService>();

        ClaimsPrincipal WithRole(UserRole role) => new(new ClaimsIdentity(
            new[] { new Claim(IamsClaims.Role, ((int)role).ToString()) }, "Test"));

        Assert.False((await authService.AuthorizeAsync(WithRole(UserRole.Manager), Policies.ManageCompany)).Succeeded);
        Assert.False((await authService.AuthorizeAsync(WithRole(UserRole.User), Policies.ManageCompany)).Succeeded);
        Assert.True((await authService.AuthorizeAsync(WithRole(UserRole.Admin), Policies.ManageCompany)).Succeeded);
        Assert.True((await authService.AuthorizeAsync(WithRole(UserRole.SuperAdmin), Policies.ManageCompany)).Succeeded);

        // A Scanner (User) can write inventory but not manage; a Viewer cannot write.
        Assert.True((await authService.AuthorizeAsync(WithRole(UserRole.User), Policies.Write)).Succeeded);
        Assert.False((await authService.AuthorizeAsync(WithRole(UserRole.Viewer), Policies.Write)).Succeeded);
    }
}
