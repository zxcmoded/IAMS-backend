using IAMS.Api.Common.Connections;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Verifies BR-TC-007/008's integrity invariant: any policy-affecting change bumps PolicyRevision in the
/// same SaveChanges. The bump is enforced centrally in IamsDbContext, so these also exercise that a raw
/// mutation (not just via the service) still bumps.
/// </summary>
public class ConnectionPolicyServiceTests
{
    private static async Task<(IamsDbContext Db, CompanyConnection Connection, Guid WarehouseId)> SeedAsync()
    {
        var db = TestDb.New();
        var tenantA = new Tenant { Id = Guid.NewGuid(), Name = "A", Kind = TenantKind.Parent };
        var tenantB = new Tenant { Id = Guid.NewGuid(), Name = "B", Kind = TenantKind.Child };
        var companyA = new Company { Id = Guid.NewGuid(), TenantId = tenantA.Id, Name = "A" };
        var companyB = new Company { Id = Guid.NewGuid(), TenantId = tenantB.Id, Name = "B" };

        // Real hierarchy under company B so scope FKs reference existing nodes (matches the SQL FKs).
        var location = new Location { Id = Guid.NewGuid(), CompanyId = companyB.Id, TenantId = tenantB.Id, Name = "Loc" };
        var warehouse = new Warehouse
        {
            Id = Guid.NewGuid(), LocationId = location.Id, CompanyId = companyB.Id, TenantId = tenantB.Id, Name = "WH"
        };

        var connection = new CompanyConnection
        {
            Id = Guid.NewGuid(),
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
        db.AddRange(tenantA, tenantB, companyA, companyB, location, warehouse, connection);
        await db.SaveChangesAsync(); // Added state -> no bump; revision stays 1
        return (db, connection, warehouse.Id);
    }

    [Fact]
    public async Task SetEnabled_BumpsPolicyRevision()
    {
        var (db, connection, _) = await SeedAsync();
        var service = new ConnectionPolicyService(db);

        await service.SetEnabledAsync(connection.Id, false, CancellationToken.None);

        Assert.Equal(2, (await db.CompanyConnections.FindAsync(connection.Id))!.PolicyRevision);
    }

    [Fact]
    public async Task SetPermissionLevel_BumpsPolicyRevision()
    {
        var (db, connection, _) = await SeedAsync();
        var service = new ConnectionPolicyService(db);

        await service.SetPermissionLevelAsync(connection.Id, PermissionLevel.Full, CancellationToken.None);

        Assert.Equal(2, (await db.CompanyConnections.FindAsync(connection.Id))!.PolicyRevision);
    }

    [Fact]
    public async Task ReplaceScopes_BumpsPolicyRevisionAndSwapsScopes()
    {
        var (db, connection, warehouseId) = await SeedAsync(); // connection starts with one company-level scope
        var service = new ConnectionPolicyService(db);

        await service.ReplaceScopesAsync(connection.Id, new[]
        {
            new CompanyConnectionScope { Level = HierarchyLevel.Warehouse, ScopeWarehouseId = warehouseId }
        }, CancellationToken.None);

        var updated = await db.CompanyConnections.FindAsync(connection.Id);
        Assert.Equal(2, updated!.PolicyRevision);

        var scopes = await db.CompanyConnectionScopes
            .Where(s => s.CompanyConnectionId == connection.Id).ToListAsync();
        Assert.Single(scopes);
        Assert.Equal(HierarchyLevel.Warehouse, scopes[0].Level);
        Assert.Equal(warehouseId, scopes[0].ScopeWarehouseId);
    }

    [Fact]
    public async Task NonPolicyChange_DoesNotBump()
    {
        var (db, connection, _) = await SeedAsync();

        // Touch an unrelated entity; the connection is untouched.
        db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Z", Kind = TenantKind.Child });
        await db.SaveChangesAsync();

        Assert.Equal(1, (await db.CompanyConnections.FindAsync(connection.Id))!.PolicyRevision);
    }
}
