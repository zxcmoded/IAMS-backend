using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Shared xUnit collection for every opt-in integration test class that runs against a REAL
/// PostgreSQL instance via <c>IAMS_PG_TEST_CONN</c> (see <see cref="SqlPolicyRevisionIntegrationTests"/>
/// and <see cref="DeviceBindingSqlConcurrencyTests"/>). xUnit parallelizes different test classes by
/// default, but never parallelizes classes within the same collection — putting both classes here
/// serializes them so they can't race each other's <c>EnsureDeletedAsync</c>/<c>EnsureCreatedAsync</c>
/// against the same hardcoded database name from the shared connection string. Without this, one
/// class's <c>EnsureDeletedAsync</c> (a Postgres <c>DROP DATABASE</c>, which force-disconnects other
/// sessions) can collide with the other class's setup/queries mid-run.
/// </summary>
[CollectionDefinition("RealPostgresIntegration")]
public class RealPostgresIntegrationCollection;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeCurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; set; } = true;
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? SessionId { get; set; }
    public bool IsSystemAdmin { get; set; }
}

public sealed class FakeHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "IAMS.Api.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

public static class TestDb
{
    /// <summary>A fresh in-memory IamsDbContext with a unique backing store per call.</summary>
    public static IamsDbContext New()
    {
        var options = new DbContextOptionsBuilder<IamsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IamsDbContext(options);
    }
}
