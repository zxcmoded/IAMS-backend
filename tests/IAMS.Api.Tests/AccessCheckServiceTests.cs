using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using Xunit;

namespace IAMS.Api.Tests;

public class AccessCheckServiceTests
{
    private static async Task<(IamsDbContext Db, FakeCurrentUser User, Guid CompanyA, Guid CompanyB, Guid ConnectionId, Guid Warehouse)> SeedAsync()
    {
        var db = TestDb.New();

        var tenantA = new Tenant { Id = Guid.NewGuid(), Name = "Parent Co", Kind = TenantKind.Parent };
        var tenantB = new Tenant { Id = Guid.NewGuid(), Name = "Child Co", Kind = TenantKind.Child };
        var companyA = new Company { Id = Guid.NewGuid(), TenantId = tenantA.Id, Name = "A" };
        var companyB = new Company { Id = Guid.NewGuid(), TenantId = tenantB.Id, Name = "B" };
        var warehouse = Guid.NewGuid();

        var connection = new CompanyConnection
        {
            Id = Guid.NewGuid(),
            SourceCompanyId = companyA.Id,
            TargetCompanyId = companyB.Id,
            ConnectionType = ConnectionType.ParentToChild,
            IsEnabled = true,
            PermissionLevel = PermissionLevel.Full,
            PolicyRevision = 7,
            Scopes = new List<CompanyConnectionScope>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Level = HierarchyLevel.Company,
                    ScopeCompanyId = companyB.Id
                }
            }
        };

        db.AddRange(tenantA, tenantB, companyA, companyB, connection);
        await db.SaveChangesAsync();

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), TenantId = tenantA.Id, CompanyId = companyA.Id };
        return (db, user, companyA.Id, companyB.Id, connection.Id, warehouse);
    }

    [Fact]
    public async Task EnabledConnection_GrantsAccess()
    {
        var (db, user, _, companyB, _, _) = await SeedAsync();
        var service = new AccessCheckService(db, user);

        var decisions = await service.EvaluateAsync(
            new[] { new AccessCheckService.Request(companyB, null, null, null, null, PermissionLevel.Read) },
            CancellationToken.None);

        Assert.True(decisions[0].Allowed);
        Assert.Equal(AccessReason.ConnectionGranted, decisions[0].Reason);
    }

    [Fact]
    public async Task DisablingConnection_TakesEffectOnNextEvaluation()
    {
        var (db, user, _, companyB, connectionId, _) = await SeedAsync();
        var service = new AccessCheckService(db, user);
        var request = new AccessCheckService.Request(companyB, null, null, null, null, PermissionLevel.Read);

        var before = await service.EvaluateAsync(new[] { request }, CancellationToken.None);
        Assert.True(before[0].Allowed);

        // Admin disables the connection (BR-TC-007) — no app update, just a data change.
        var connection = await db.CompanyConnections.FindAsync(connectionId);
        connection!.IsEnabled = false;
        await db.SaveChangesAsync();

        var after = await service.EvaluateAsync(new[] { request }, CancellationToken.None);
        Assert.False(after[0].Allowed);
        Assert.Equal(AccessReason.ConnectionDisabled, after[0].Reason);
    }

    [Fact]
    public async Task UnknownTargetCompany_FailsClosed()
    {
        var (db, user, _, _, _, _) = await SeedAsync();
        var service = new AccessCheckService(db, user);

        var decisions = await service.EvaluateAsync(
            new[] { new AccessCheckService.Request(Guid.NewGuid(), null, null, null, null, PermissionLevel.Read) },
            CancellationToken.None);

        Assert.False(decisions[0].Allowed);
        Assert.Equal(AccessReason.NoConnection, decisions[0].Reason);
    }

    [Fact]
    public async Task Batch_PreservesOrderAcrossMixedTargets()
    {
        var (db, user, companyA, companyB, _, _) = await SeedAsync();
        var service = new AccessCheckService(db, user);

        var requests = new[]
        {
            new AccessCheckService.Request(companyA, null, null, null, null, PermissionLevel.Full), // same tenant
            new AccessCheckService.Request(companyB, null, null, null, null, PermissionLevel.Full), // connected
            new AccessCheckService.Request(Guid.NewGuid(), null, null, null, null, PermissionLevel.Read) // unknown
        };

        var decisions = await service.EvaluateAsync(requests, CancellationToken.None);

        Assert.Equal(3, decisions.Count);
        Assert.Equal(AccessReason.SameTenant, decisions[0].Reason);
        Assert.Equal(AccessReason.ConnectionGranted, decisions[1].Reason);
        Assert.Equal(AccessReason.NoConnection, decisions[2].Reason);
    }

    [Fact]
    public async Task ConnectionFilters_AreCurrentlyIgnored_DocumentsKnownGap()
    {
        // Documents the code-review-flagged gap: CompanyConnectionFilter rows are persisted (and bump
        // PolicyRevision) but EffectiveAccessEvaluator/AccessCheckService do not consult them yet. A filter
        // that an admin might set expecting it to narrow access has NO effect today. This test pins that
        // current behavior so full filter enforcement isn't silently forgotten when filters become
        // configurable. If/when filters are enforced, this test should be updated to assert the denial.
        var (db, user, _, companyB, connectionId, _) = await SeedAsync();

        var connection = await db.CompanyConnections.FindAsync(connectionId);
        db.CompanyConnectionFilters.Add(new CompanyConnectionFilter
        {
            Id = Guid.NewGuid(),
            CompanyConnectionId = connection!.Id,
            FilterType = ConnectionFilterType.Region,
            FilterValue = "A-REGION-THAT-WOULD-EXCLUDE-THIS-RESOURCE"
        });
        await db.SaveChangesAsync();

        var service = new AccessCheckService(db, user);
        var decisions = await service.EvaluateAsync(
            new[] { new AccessCheckService.Request(companyB, null, null, null, null, PermissionLevel.Read) },
            CancellationToken.None);

        // Access is still granted — the filter is not consulted (current, documented behavior).
        Assert.True(decisions[0].Allowed);
        Assert.Equal(AccessReason.ConnectionGranted, decisions[0].Reason);
    }

    [Fact]
    public async Task GetActorPolicyVersion_ReturnsMaxAcrossConnections()
    {
        var (db, user, _, _, _, _) = await SeedAsync();
        var service = new AccessCheckService(db, user);

        var version = await service.GetActorPolicyVersionAsync(CancellationToken.None);

        Assert.Equal(7, version);
    }
}
