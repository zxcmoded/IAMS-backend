using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using IAMS.Api.Features.Auth.VerifyTwoFactor;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Opt-in integration tests exercising VerifyTwoFactorHandler's device-binding races against a REAL SQL
/// Server: (1) two concurrent first-time registrations (no row exists yet), (2) two concurrent
/// re-registrations against the SAME just-reset row (an UPDATE race has no unique index to catch it), (3) a
/// harmless same-device double-tap/retry race, and (4) a genuinely FORCED conflict proving the match
/// branch's catch really defers to the shared resolver instead of blindly proceeding — see that test's own
/// comment for why it's built with explicit two-context sequencing rather than <c>Task.WhenAll</c>
/// (real-time racing of two full handlers was tried and found by code review to exercise the actual bug
/// path only ~7% of the time, since an admin reset is far cheaper than a PBKDF2-laden verify).
///
/// Deliberately NOT run against the InMemory provider (see <c>TestSupport.TestDb</c>): InMemory does not
/// enforce the real unique index on <see cref="UserDeviceBinding.UserId"/>, nor does it model row-level
/// locking / RowVersion conflicts the same way, so none of these races are observable on it. Skipped unless
/// <c>IAMS_SQL_TEST_CONN</c> is set, matching <see cref="SqlPolicyRevisionIntegrationTests"/>. To run:
///   IAMS_SQL_TEST_CONN="Server=localhost,1433;Database=IAMS_QATest;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False;" dotnet test
/// </summary>
public class DeviceBindingSqlConcurrencyTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("IAMS_SQL_TEST_CONN");

    private static readonly IOptions<JwtOptions> Options = Microsoft.Extensions.Options.Options.Create(new JwtOptions
    {
        SigningKey = "test-signing-key-that-is-long-enough-32b",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
        TwoFactorCodeTtlSeconds = 300,
        TwoFactorResendCooldownSeconds = 30,
        TwoFactorMaxAttempts = 5
    });

    private static IamsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IamsDbContext>().UseSqlServer(Conn).Options);

    private sealed record Seed(Guid UserId, Guid AdminId, string Token1, string Otp1, string Token2, string Otp2);

    /// <summary>
    /// Seeds a user + a real system-admin user + two fresh OTP challenges. <paramref name="existingBindingFactory"/>,
    /// if provided, is called with the seeded admin's real id (so a binding can reference it as
    /// <see cref="UserDeviceBinding.ResetByUserId"/> without a dangling FK) and the result is attached up
    /// front, so the race starts from "one row already exists" instead of "no row exists".
    /// </summary>
    private static async Task<Seed> SeedAsync(Func<Guid, UserDeviceBinding>? existingBindingFactory = null)
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Kind = TenantKind.Parent };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "C" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "race",
            NormalizedUsername = "RACE",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = true
        };
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            NormalizedUsername = "ADMIN",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = true,
            IsSystemAdmin = true
        };
        var membership = new UserCompanyMembership
        {
            Id = Guid.NewGuid(), UserId = user.Id, CompanyId = company.Id, IsPrimary = true
        };

        const string otp1 = "111111";
        const string otp2 = "222222";
        var challenge1 = new OtpChallenge
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ChallengeToken = "race-token-1",
            CodeHash = PasswordHasher.Hash(otp1),
            Purpose = OtpPurpose.Login,
            Channel = TwoFactorChannel.Email,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            ResendAvailableAtUtc = DateTime.UtcNow
        };
        var challenge2 = new OtpChallenge
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ChallengeToken = "race-token-2",
            CodeHash = PasswordHasher.Hash(otp2),
            Purpose = OtpPurpose.Login,
            Channel = TwoFactorChannel.Email,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            ResendAvailableAtUtc = DateTime.UtcNow
        };

        db.AddRange(tenant, company, user, admin, membership, challenge1, challenge2);
        if (existingBindingFactory is not null)
        {
            var existingBinding = existingBindingFactory(admin.Id);
            existingBinding.UserId = user.Id;
            db.UserDeviceBindings.Add(existingBinding);
        }
        await db.SaveChangesAsync();
        return new Seed(user.Id, admin.Id, challenge1.ChallengeToken, otp1, challenge2.ChallengeToken, otp2);
    }

    private static Task<Results<Ok<Features.Auth.AuthTokenResponse>, ProblemHttpResult>> RunVerifyAsync(
        IamsDbContext db, FakeClock clock, string token, string otp, string deviceId)
    {
        var handler = new VerifyTwoFactorHandler(
            db, new SessionIssuer(db, new JwtTokenService(Options), clock, Options), new ActiveScopeResolver(db),
            clock, Options, NullLogger<VerifyTwoFactorHandler>.Instance);
        return handler.HandleAsync(new VerifyTwoFactorCommand(token, otp, deviceId), CancellationToken.None);
    }

    private static void AssertExactlyOneWinnerOneMismatch(
        Results<Ok<Features.Auth.AuthTokenResponse>, ProblemHttpResult>[] results)
    {
        var okCount = results.Count(r => r.Result is Ok<Features.Auth.AuthTokenResponse>);
        var mismatchCount = results.Count(r =>
            r.Result is ProblemHttpResult p &&
            p.StatusCode == StatusCodes.Status403Forbidden &&
            Equals(p.ProblemDetails.Extensions["code"], ErrorCodes.DeviceMismatch));

        // Exactly one request wins the race and gets a session; the other is cleanly rejected as a device
        // mismatch (different device ids were used, so a "same device retried" outcome is impossible here).
        // Neither call should have thrown — Task.WhenAll would have surfaced that as a test failure already.
        Assert.Equal(1, okCount);
        Assert.Equal(1, mismatchCount);
    }

    [Fact]
    public async Task ConcurrentFirstVerify_DifferentDevices_OneWinsOneGetsCleanMismatch_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set
        var seed = await SeedAsync();

        // Two independent DbContexts against the SAME real database, exactly like two separate HTTP
        // requests each getting their own scoped context — this is what makes the race observable.
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        // Fire both "requests" at the real SQL Server at the same time — no unhandled exception must
        // escape either call.
        var taskA = RunVerifyAsync(dbA, clock, seed.Token1, seed.Otp1, "device-A");
        var taskB = RunVerifyAsync(dbB, clock, seed.Token2, seed.Otp2, "device-B");
        var results = await Task.WhenAll(taskA, taskB);

        AssertExactlyOneWinnerOneMismatch(results);

        await using var verify = NewContext();
        var bindings = await verify.UserDeviceBindings.AsNoTracking()
            .Where(b => b.UserId == seed.UserId).ToListAsync();
        var binding = Assert.Single(bindings); // the unique index held: exactly one row, never two
        Assert.Equal(DeviceBindingStatus.Active, binding.Status);
        Assert.True(binding.DeviceId is "device-A" or "device-B");
    }

    [Fact]
    public async Task ConcurrentReVerify_AfterAdminReset_DifferentDevices_OneWinsOneGetsCleanMismatch_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set

        // Start from an already-Reset row (as if an admin had just reset this user's binding) — this is the
        // UPDATE-branch race code review flagged: no unique index fires on an UPDATE, so without the
        // conditional ExecuteUpdateAsync guard, two concurrent re-registrations could both "win".
        var existingBindingId = Guid.NewGuid();
        var seed = await SeedAsync(adminId => new UserDeviceBinding
        {
            Id = existingBindingId,
            DeviceId = "old-device",
            Status = DeviceBindingStatus.Reset,
            RegisteredAtUtc = DateTime.UtcNow.AddDays(-30),
            LastAuthenticatedAtUtc = DateTime.UtcNow.AddDays(-1),
            ResetAtUtc = DateTime.UtcNow.AddMinutes(-5),
            ResetByUserId = adminId
        });

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        var taskA = RunVerifyAsync(dbA, clock, seed.Token1, seed.Otp1, "device-C");
        var taskB = RunVerifyAsync(dbB, clock, seed.Token2, seed.Otp2, "device-D");
        var results = await Task.WhenAll(taskA, taskB);

        AssertExactlyOneWinnerOneMismatch(results);

        await using var verify = NewContext();
        var bindings = await verify.UserDeviceBindings.AsNoTracking()
            .Where(b => b.UserId == seed.UserId).ToListAsync();
        // The row is REUSED (same Id), never duplicated — the unique index on UserId was never at risk of
        // being violated here in the first place, which is exactly why this race needed its own guard.
        var binding = Assert.Single(bindings);
        Assert.Equal(existingBindingId, binding.Id);
        Assert.Equal(DeviceBindingStatus.Active, binding.Status);
        Assert.True(binding.DeviceId is "device-C" or "device-D");
        // The prior reset's audit trail describes the OLD (now-superseded) registration and must not
        // linger on the freshly re-activated binding, regardless of which concurrent request won.
        Assert.Null(binding.ResetAtUtc);
        Assert.Null(binding.ResetByUserId);
    }

    [Fact]
    public async Task ConcurrentReVerify_SameAlreadyActiveDevice_BothSucceed_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set

        // Regression coverage for the bug code review found in the SECOND fix: adding RowVersion to guard
        // the reuse branch also silently subjected this THIRD path — the "device already matches" branch,
        // which only touches LastAuthenticatedAtUtc — to the same concurrency check. A completely normal
        // double-tap-Verify or client-timeout-retry for an ALREADY-bound device must never crash; both
        // concurrent requests are legitimate and both must succeed.
        var existingBindingId = Guid.NewGuid();
        var seed = await SeedAsync(_ => new UserDeviceBinding
        {
            Id = existingBindingId,
            DeviceId = "already-bound-device",
            Status = DeviceBindingStatus.Active,
            RegisteredAtUtc = DateTime.UtcNow.AddDays(-1),
            LastAuthenticatedAtUtc = DateTime.UtcNow.AddDays(-1)
        });

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        // Both requests present the SAME already-bound device — no unhandled exception must escape either
        // call, and BOTH must succeed (unlike the other two races, there is no legitimate "loser" here).
        var taskA = RunVerifyAsync(dbA, clock, seed.Token1, seed.Otp1, "already-bound-device");
        var taskB = RunVerifyAsync(dbB, clock, seed.Token2, seed.Otp2, "already-bound-device");
        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.IsType<Ok<Features.Auth.AuthTokenResponse>>(r.Result));

        await using var verify = NewContext();
        var bindings = await verify.UserDeviceBindings.AsNoTracking()
            .Where(b => b.UserId == seed.UserId).ToListAsync();
        var binding = Assert.Single(bindings); // still the same row, never duplicated
        Assert.Equal(existingBindingId, binding.Id);
        Assert.Equal(DeviceBindingStatus.Active, binding.Status);
        Assert.Equal("already-bound-device", binding.DeviceId);
    }

    [Fact]
    public async Task MatchBranchConflict_WinnerIsDifferentDevice_RejectsInsteadOfSilentlyProceeding_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set

        // Regression coverage for the THIRD-round finding, replacing an earlier version of this test that
        // raced two FULL handlers via Task.WhenAll. Code review instrumented that version and found the
        // admin-reset side (one trivial query+update) always wins and fully commits before a verify handler
        // even finishes its own PBKDF2 check (100k iterations) — so the match branch's actual
        // DbUpdateConcurrencyException catch was NEVER exercised: reverting the fix back to the round-3
        // "just proceed" bug still passed the old test 14/15 times. A test that stays green ~93% of the
        // time with the exact bug it exists to catch reintroduced is worthless.
        //
        // This version forces the conflict deterministically instead of racing wall-clock timing:
        //   1. Pre-track the binding in the SAME DbContext the handler will use, capturing the genuinely
        //      current RowVersion (exactly what the handler's own internal query would capture).
        //   2. A SEPARATE, fully-committed write then changes ONLY the bound device (Status stays Active,
        //      so the handler's OWN "is there an active binding" query still finds a row and takes the
        //      match branch — exactly as it would in the wild; nothing here relies on identity-map trickery
        //      to fake the SQL predicate itself, only on EF's documented behavior of returning the SAME
        //      tracked instance for an already-tracked key instead of overwriting it from a fresh query).
        //   3. The real handler is invoked on that same context. It believes the presented device still
        //      matches (its own copy is stale). Its SaveChangesAsync then genuinely conflicts — not by
        //      chance, because the actual current RowVersion has moved.
        // The round-3 bug and the fix are OBSERVABLY DIFFERENT on this exact sequence: buggy code proceeds
        // and issues a session regardless (200); fixed code notices the real winner is a different device
        // and rejects (403). See DeviceBindingTests.ResolveConcurrentWinnerAsync_* for direct, deterministic
        // coverage of the resolver's OTHER outcome (winner no longer Active) — that specific interleaving
        // cannot be forced this way, since the handler's own initial query would legitimately stop matching
        // "Active" the moment a real reset lands, taking it out of the match branch entirely before any
        // conflict is possible; see that test file for why.
        var existingBindingId = Guid.NewGuid();
        var seed = await SeedAsync(_ => new UserDeviceBinding
        {
            Id = existingBindingId,
            DeviceId = "original-device",
            Status = DeviceBindingStatus.Active,
            RegisteredAtUtc = DateTime.UtcNow.AddDays(-1),
            LastAuthenticatedAtUtc = DateTime.UtcNow.AddDays(-1)
        });

        await using var dbVerify = NewContext();
        // Step 1: pre-track with the SAME context HandleAsync will use below.
        _ = await dbVerify.UserDeviceBindings
            .FirstAsync(b => b.UserId == seed.UserId && b.Status == DeviceBindingStatus.Active);

        // Step 2: a genuinely separate, fully-committed write changes the bound device. Status stays
        // Active throughout, so the handler's own filtered query below still finds a match.
        await using (var dbOtherDevice = NewContext())
        {
            var otherDeviceRow = await dbOtherDevice.UserDeviceBindings.FirstAsync(b => b.UserId == seed.UserId);
            otherDeviceRow.DeviceId = "winning-device";
            otherDeviceRow.LastAuthenticatedAtUtc = DateTime.UtcNow;
            await dbOtherDevice.SaveChangesAsync();
        }

        // Step 3: the real handler, using dbVerify (which still holds the stale pre-tracked entity),
        // presents the ORIGINAL device. It must NOT get a session for a binding that has genuinely moved to
        // another device underneath it.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var result = await RunVerifyAsync(dbVerify, clock, seed.Token1, seed.Otp1, "original-device");

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(ErrorCodes.DeviceMismatch, problem.ProblemDetails.Extensions["code"]);

        await using var verify = NewContext();
        var binding = await verify.UserDeviceBindings.AsNoTracking().SingleAsync(b => b.UserId == seed.UserId);
        Assert.Equal(existingBindingId, binding.Id); // reused, never duplicated
        Assert.Equal("winning-device", binding.DeviceId); // untouched by the rejected request
    }
}
