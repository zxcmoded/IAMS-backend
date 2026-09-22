using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Features.Auth.Activate;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Opt-in integration tests exercising <see cref="ActivateHandler"/>'s single-device-binding race against a
/// REAL PostgreSQL instance. Ports the approach proven in the old (now-removed) device-binding concurrency
/// suite onto the new schema: binding fields live directly on <see cref="User"/> now, so activating a
/// never-activated key and re-activating a just-reset key are BOTH plain UPDATEs to the same row (there is
/// nothing to INSERT — the key already exists as a column) — one race shape instead of the old
/// insert-race/update-race split, closed by the same `xmin`-backed optimistic-concurrency token.
///
/// Deliberately NOT run against the InMemory provider (see <c>TestSupport.TestDb</c>): InMemory does not
/// model the `xmin`-based concurrency token, so the race is not observable on it. Skipped unless
/// <c>IAMS_PG_TEST_CONN</c> is set, matching <see cref="SqlPolicyRevisionIntegrationTests"/>. To run:
///   IAMS_PG_TEST_CONN="Host=localhost;Port=5432;Database=iams_qatest;Username=iams;Password=..." dotnet test
///
/// In the <c>"RealPostgresIntegration"</c> xUnit collection alongside the other real-Postgres suites so
/// they never run concurrently against the same hardcoded database name.
/// </summary>
[Collection("RealPostgresIntegration")]
public class ActivationSqlConcurrencyTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("IAMS_PG_TEST_CONN");

    private static readonly IOptions<JwtOptions> Options = Microsoft.Extensions.Options.Options.Create(new JwtOptions
    {
        SigningKey = "test-signing-key-that-is-long-enough-32b",
        AccessTokenLifetimeDays = 36500
    });

    private static IamsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IamsDbContext>().UseNpgsql(Conn).Options);

    private const string RawKey = "race-activation-key";

    private sealed record Seed(Guid UserId);

    /// <summary>
    /// Seeds a single user with the shared <see cref="RawKey"/>. <paramref name="initialState"/>, if
    /// provided, is applied to the user before the first save — e.g. to start the race from an
    /// already-Activated-then-Reset state instead of a truly virgin key.
    /// </summary>
    private static async Task<Seed> SeedAsync(Action<User>? initialState = null)
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "C" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "race",
            CompanyId = company.Id,
            Role = UserRole.User,
            ActivationKeyHash = TokenGenerator.Sha256(RawKey),
            ActivationStatus = ActivationStatus.NotActivated,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = true
        };
        initialState?.Invoke(user);

        db.AddRange(company, user);
        await db.SaveChangesAsync();
        return new Seed(user.Id);
    }

    private static Task<Results<Ok<Features.Auth.AuthTokenResponse>, ProblemHttpResult>> RunActivateAsync(
        IamsDbContext db, FakeClock clock, string deviceId)
    {
        var handler = new ActivateHandler(
            db, new SessionIssuer(db, new JwtTokenService(Options), clock), clock);
        return handler.HandleAsync(new ActivateCommand(RawKey, deviceId), CancellationToken.None);
    }

    private static void AssertExactlyOneWinnerOneRejection(
        Results<Ok<Features.Auth.AuthTokenResponse>, ProblemHttpResult>[] results)
    {
        var okCount = results.Count(r => r.Result is Ok<Features.Auth.AuthTokenResponse>);
        var rejectedCount = results.Count(r =>
            r.Result is ProblemHttpResult p &&
            p.StatusCode == StatusCodes.Status403Forbidden &&
            Equals(p.ProblemDetails.Extensions["code"], ErrorCodes.ActivationKeyAlreadyBound));

        // Exactly one request wins the race and gets a session; the other is cleanly rejected as
        // already-bound (different device ids were used, so "same device retried" is impossible here).
        // Neither call should have thrown — Task.WhenAll would have surfaced that as a test failure already.
        Assert.Equal(1, okCount);
        Assert.Equal(1, rejectedCount);
    }

    [Fact]
    public async Task ConcurrentActivate_FreshKey_DifferentDevices_OneWinsOneGetsCleanRejection_OnRealPostgres()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_PG_TEST_CONN is set
        var seed = await SeedAsync();

        // Two independent DbContexts against the SAME real database, exactly like two separate HTTP
        // requests each getting their own scoped context — this is what makes the race observable.
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var taskA = RunActivateAsync(dbA, clock, "device-A");
        var taskB = RunActivateAsync(dbB, clock, "device-B");
        var results = await Task.WhenAll(taskA, taskB);

        AssertExactlyOneWinnerOneRejection(results);

        await using var verify = NewContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == seed.UserId);
        Assert.Equal(ActivationStatus.Activated, user.ActivationStatus);
        Assert.True(user.ActivatedDeviceId is "device-A" or "device-B");
    }

    [Fact]
    public async Task ConcurrentActivate_AfterAdminReset_DifferentDevices_OneWinsOneGetsCleanRejection_OnRealPostgres()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_PG_TEST_CONN is set

        // Start from an already-Reset user (as if an admin had just reset this user's activation) — this is
        // the branch code review would flag first: NotActivated-via-reset must race exactly like
        // NotActivated-via-never-used, since both are the identical UPDATE-vs-UPDATE race on the same row.
        var adminId = Guid.NewGuid();
        var seed = await SeedAsync(user =>
        {
            user.ActivationStatus = ActivationStatus.NotActivated;
            user.ActivatedDeviceId = "old-device";
            user.ActivatedAtUtc = DateTime.UtcNow.AddDays(-30);
            user.ActivationResetAtUtc = DateTime.UtcNow.AddMinutes(-5);
            user.ActivationResetByUserId = null; // avoid a dangling FK to a non-seeded admin row
        });

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var taskA = RunActivateAsync(dbA, clock, "device-C");
        var taskB = RunActivateAsync(dbB, clock, "device-D");
        var results = await Task.WhenAll(taskA, taskB);

        AssertExactlyOneWinnerOneRejection(results);

        await using var verify = NewContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == seed.UserId);
        Assert.Equal(seed.UserId, user.Id); // same row, never duplicated
        Assert.Equal(ActivationStatus.Activated, user.ActivationStatus);
        Assert.True(user.ActivatedDeviceId is "device-C" or "device-D");
        // The prior reset's audit trail describes the OLD (now-superseded) registration and must not
        // linger on the freshly re-activated binding, regardless of which concurrent request won.
        Assert.Null(user.ActivationResetAtUtc);
        Assert.Null(user.ActivationResetByUserId);
    }

    [Fact]
    public async Task ConcurrentReActivate_SameAlreadyBoundDevice_BothSucceed_OnRealPostgres()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_PG_TEST_CONN is set

        // Regression coverage: a completely normal double-tap-Activate or client-timeout-retry from the
        // SAME already-bound device must never crash or spuriously reject — both concurrent requests are
        // legitimate and both must succeed. This branch performs no write at all (nothing to conflict on),
        // so this is mostly a "does not throw" / "does not misclassify" guard.
        var seed = await SeedAsync(user =>
        {
            user.ActivationStatus = ActivationStatus.Activated;
            user.ActivatedDeviceId = "already-bound-device";
            user.ActivatedAtUtc = DateTime.UtcNow.AddDays(-1);
        });

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var taskA = RunActivateAsync(dbA, clock, "already-bound-device");
        var taskB = RunActivateAsync(dbB, clock, "already-bound-device");
        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(r.Result));

        await using var verify = NewContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == seed.UserId);
        Assert.Equal(ActivationStatus.Activated, user.ActivationStatus);
        Assert.Equal("already-bound-device", user.ActivatedDeviceId);
    }
}
