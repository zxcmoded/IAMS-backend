using IAMS.Api.Common.Connections;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Opt-in integration tests that exercise the BR-TC-007/008 PolicyRevision invariant against a REAL
/// SQL Server (the in-memory provider can mask provider-specific ChangeTracker/transaction behavior).
/// Added by QA to close the "never applied/verified against a live database" gap.
///
/// These are skipped unless <c>IAMS_SQL_TEST_CONN</c> is set to a SQL Server connection string, so the
/// default <c>dotnet test</c> run is unchanged. To run:
///   IAMS_SQL_TEST_CONN="Server=localhost,1433;Database=IAMS_QATest;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False;" dotnet test
/// Each test builds a fresh schema (EnsureDeleted + EnsureCreated) so runs are independent.
/// </summary>
public class SqlPolicyRevisionIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("IAMS_SQL_TEST_CONN");

    private static IamsDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<IamsDbContext>()
            .UseSqlServer(Conn)
            .Options;
        return new IamsDbContext(options);
    }

    private sealed record Seed(Guid ConnectionId, Guid WarehouseId);

    private static async Task<Seed> SeedConnectionAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var tenantA = new Tenant { Id = Guid.NewGuid(), Name = "A", Kind = TenantKind.Parent };
        var tenantB = new Tenant { Id = Guid.NewGuid(), Name = "B", Kind = TenantKind.Child };
        var companyA = new Company { Id = Guid.NewGuid(), TenantId = tenantA.Id, Name = "A" };
        var companyB = new Company { Id = Guid.NewGuid(), TenantId = tenantB.Id, Name = "B" };
        // A real Location + Warehouse under companyB: on a real SQL Server the scope→Warehouse FK is
        // enforced, so a scope must reference an existing node (the in-memory provider ignores this).
        var locationB = new Location { Id = Guid.NewGuid(), Name = "L", TenantId = tenantB.Id, CompanyId = companyB.Id };
        var warehouseB = new Warehouse { Id = Guid.NewGuid(), Name = "W", TenantId = tenantB.Id, LocationId = locationB.Id, CompanyId = companyB.Id };
        var connectionId = Guid.NewGuid();
        var connection = new CompanyConnection
        {
            Id = connectionId,
            SourceCompanyId = companyA.Id,
            TargetCompanyId = companyB.Id,
            ConnectionType = ConnectionType.ParentToChild,
            IsEnabled = true,
            PermissionLevel = PermissionLevel.Read,
            PolicyRevision = 1,
            Scopes = new List<CompanyConnectionScope>
            {
                new() { Id = Guid.NewGuid(), Level = HierarchyLevel.Company, ScopeCompanyId = companyB.Id }
            }
        };
        db.AddRange(tenantA, tenantB, companyA, companyB, locationB, warehouseB, connection);
        await db.SaveChangesAsync(); // Added → no bump, revision stays 1
        return new Seed(connectionId, warehouseB.Id);
    }

    [Fact]
    public async Task SetEnabled_BumpsPolicyRevision_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set
        var id = (await SeedConnectionAsync()).ConnectionId;

        await using (var db = NewContext())
        {
            await new ConnectionPolicyService(db).SetEnabledAsync(id, false, CancellationToken.None);
        }

        // Re-read from a FRESH context: the change must be durably persisted with a bumped revision,
        // proving IsEnabled and PolicyRevision were written in the same SaveChanges/transaction.
        await using (var verify = NewContext())
        {
            var c = await verify.CompanyConnections.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.False(c.IsEnabled);
            Assert.Equal(2, c.PolicyRevision);
        }
    }

    [Fact]
    public async Task SetPermissionLevel_BumpsPolicyRevision_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set
        var id = (await SeedConnectionAsync()).ConnectionId;

        await using (var db = NewContext())
        {
            await new ConnectionPolicyService(db).SetPermissionLevelAsync(id, PermissionLevel.Full, CancellationToken.None);
        }

        await using (var verify = NewContext())
        {
            var c = await verify.CompanyConnections.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(PermissionLevel.Full, c.PermissionLevel);
            Assert.Equal(2, c.PolicyRevision);
        }
    }

    [Fact]
    public async Task ReplaceScopes_BumpsPolicyRevisionOnce_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set
        var seed = await SeedConnectionAsync();
        var id = seed.ConnectionId;
        var newWarehouse = seed.WarehouseId; // a real warehouse so the scope→Warehouse FK is satisfied

        await using (var db = NewContext())
        {
            // Two scope mutations (remove old company scope + add warehouse scope) in ONE SaveChanges
            // must bump the revision by exactly 1, not once per row.
            await new ConnectionPolicyService(db).ReplaceScopesAsync(id, new[]
            {
                new CompanyConnectionScope { Level = HierarchyLevel.Warehouse, ScopeWarehouseId = newWarehouse }
            }, CancellationToken.None);
        }

        await using (var verify = NewContext())
        {
            var c = await verify.CompanyConnections.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(2, c.PolicyRevision);
            var scopes = await verify.CompanyConnectionScopes.AsNoTracking()
                .Where(s => s.CompanyConnectionId == id).ToListAsync();
            Assert.Single(scopes);
            Assert.Equal(HierarchyLevel.Warehouse, scopes[0].Level);
            Assert.Equal(newWarehouse, scopes[0].ScopeWarehouseId);
        }
    }

    [Fact]
    public async Task NonPolicyChange_DoesNotBump_OnRealSqlServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in: skipped unless IAMS_SQL_TEST_CONN is set
        var id = (await SeedConnectionAsync()).ConnectionId;

        await using (var db = NewContext())
        {
            db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Z", Kind = TenantKind.Child });
            await db.SaveChangesAsync();
        }

        await using (var verify = NewContext())
        {
            var c = await verify.CompanyConnections.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(1, c.PolicyRevision);
        }
    }
}
