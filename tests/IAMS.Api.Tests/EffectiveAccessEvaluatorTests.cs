using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using Xunit;

namespace IAMS.Api.Tests;

public class EffectiveAccessEvaluatorTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();

    private static AccessActor Actor => new(TenantA, CompanyA);

    private static ResourceDescriptor ResourceInB(
        Guid? location = null, Guid? warehouse = null, Guid? rack = null, Guid? bin = null) =>
        new(TenantB, CompanyB, location, warehouse, rack, bin);

    private static ConnectionEvaluation Connection(
        bool enabled = true,
        PermissionLevel basePermission = PermissionLevel.Full,
        long policyRevision = 1,
        params ScopeGrant[] scopes) =>
        new(Guid.NewGuid(), enabled, basePermission, policyRevision, scopes.ToList());

    // Convenience: a connection with a single whole-company scope.
    private static ConnectionEvaluation CompanyScoped(
        bool enabled = true, PermissionLevel permission = PermissionLevel.Full) =>
        Connection(enabled, permission, 1, new ScopeGrant(HierarchyLevel.Company, CompanyB, null));

    [Fact]
    public void SameTenant_IsAllowed_WithoutAnyConnection()
    {
        var resource = new ResourceDescriptor(TenantA, CompanyA, null, null, null, null);

        var d = EffectiveAccessEvaluator.Evaluate(Actor, resource, PermissionLevel.Full, null);

        Assert.True(d.Allowed);
        Assert.Equal(AccessReason.SameTenant, d.Reason);
    }

    [Fact]
    public void CrossTenant_NoConnection_IsDenied()
    {
        var d = EffectiveAccessEvaluator.Evaluate(Actor, ResourceInB(), PermissionLevel.Read, null);

        Assert.False(d.Allowed);
        Assert.Equal(AccessReason.NoConnection, d.Reason);
        Assert.Null(d.EffectivePermission);
    }

    [Fact]
    public void CrossTenant_DisabledConnection_IsDenied()
    {
        var d = EffectiveAccessEvaluator.Evaluate(Actor, ResourceInB(), PermissionLevel.Read, CompanyScoped(enabled: false));

        Assert.False(d.Allowed);
        Assert.Equal(AccessReason.ConnectionDisabled, d.Reason);
    }

    [Fact]
    public void EnabledConnection_WithNoScopes_IsOutOfScope()
    {
        var connection = Connection(enabled: true); // zero scopes

        var d = EffectiveAccessEvaluator.Evaluate(Actor, ResourceInB(), PermissionLevel.Read, connection);

        Assert.False(d.Allowed);
        Assert.Equal(AccessReason.OutOfScope, d.Reason);
    }

    [Fact]
    public void CompanyScopedConnection_GrantsAccessToResourceAnywhereInCompany()
    {
        var connection = CompanyScoped(permission: PermissionLevel.Write);
        var resource = ResourceInB(location: Guid.NewGuid(), warehouse: Guid.NewGuid());

        var d = EffectiveAccessEvaluator.Evaluate(Actor, resource, PermissionLevel.Write, connection);

        Assert.True(d.Allowed);
        Assert.Equal(AccessReason.ConnectionGranted, d.Reason);
        Assert.Equal(PermissionLevel.Write, d.EffectivePermission);
    }

    [Fact]
    public void WarehouseScopedConnection_DeniesResourceInDifferentWarehouse()
    {
        var scopedWarehouse = Guid.NewGuid();
        var connection = Connection(scopes: new ScopeGrant(HierarchyLevel.Warehouse, scopedWarehouse, null));
        var resource = ResourceInB(warehouse: Guid.NewGuid());

        var d = EffectiveAccessEvaluator.Evaluate(Actor, resource, PermissionLevel.Read, connection);

        Assert.False(d.Allowed);
        Assert.Equal(AccessReason.OutOfScope, d.Reason);
    }

    [Fact]
    public void WarehouseScopedConnection_AllowsResourceInScopedWarehouse()
    {
        var scopedWarehouse = Guid.NewGuid();
        var connection = Connection(scopes: new ScopeGrant(HierarchyLevel.Warehouse, scopedWarehouse, null));
        var resource = ResourceInB(warehouse: scopedWarehouse);

        var d = EffectiveAccessEvaluator.Evaluate(Actor, resource, PermissionLevel.Read, connection);

        Assert.True(d.Allowed);
        Assert.Equal(AccessReason.ConnectionGranted, d.Reason);
    }

    [Fact]
    public void DeeperScopedConnection_FailsClosed_WhenResourceDoesNotResolveThatDeep()
    {
        // Scoped to a specific bin, but the resource only carries company/location coordinates.
        var connection = Connection(scopes: new ScopeGrant(HierarchyLevel.Bin, Guid.NewGuid(), null));
        var resource = ResourceInB(location: Guid.NewGuid());

        var d = EffectiveAccessEvaluator.Evaluate(Actor, resource, PermissionLevel.Read, connection);

        Assert.False(d.Allowed);
        Assert.Equal(AccessReason.OutOfScope, d.Reason);
    }

    [Theory]
    [InlineData(PermissionLevel.Read, PermissionLevel.Write)]  // granted Read < required Write -> deny
    [InlineData(PermissionLevel.Write, PermissionLevel.Read)]  // granted Write >= required Read -> allow
    [InlineData(PermissionLevel.Read, PermissionLevel.Read)]
    [InlineData(PermissionLevel.Full, PermissionLevel.Full)]
    [InlineData(PermissionLevel.Write, PermissionLevel.Full)]  // granted Write < required Full -> deny
    public void PermissionSufficiency_IsOrdered(PermissionLevel granted, PermissionLevel required)
    {
        var connection = CompanyScoped(permission: granted);

        var d = EffectiveAccessEvaluator.Evaluate(Actor, ResourceInB(), required, connection);

        var expected = granted >= required;
        Assert.Equal(expected, d.Allowed);
        Assert.Equal(expected ? AccessReason.ConnectionGranted : AccessReason.InsufficientPermission, d.Reason);
    }

    [Fact]
    public void InsufficientPermission_ReportsBestAvailablePermission()
    {
        var connection = CompanyScoped(permission: PermissionLevel.Read);

        var d = EffectiveAccessEvaluator.Evaluate(Actor, ResourceInB(), PermissionLevel.Full, connection);

        Assert.False(d.Allowed);
        Assert.Equal(AccessReason.InsufficientPermission, d.Reason);
        Assert.Equal(PermissionLevel.Read, d.EffectivePermission);
    }

    [Fact]
    public void MultipleScopes_MostPermissiveInScopeWins_ViaOverride()
    {
        var scopedWarehouse = Guid.NewGuid();
        // Base Read across the company, but a Full override on one warehouse.
        var connection = Connection(
            basePermission: PermissionLevel.Read,
            scopes: new[]
            {
                new ScopeGrant(HierarchyLevel.Company, CompanyB, null),
                new ScopeGrant(HierarchyLevel.Warehouse, scopedWarehouse, PermissionLevel.Full)
            });
        var resource = ResourceInB(warehouse: scopedWarehouse);

        var d = EffectiveAccessEvaluator.Evaluate(Actor, resource, PermissionLevel.Full, connection);

        Assert.True(d.Allowed);
        Assert.Equal(PermissionLevel.Full, d.EffectivePermission);
    }

    [Fact]
    public void MultipleScopes_OutOfScopeOnesAreIgnored()
    {
        var scopedWarehouse = Guid.NewGuid();
        var connection = Connection(
            basePermission: PermissionLevel.Read,
            scopes: new[]
            {
                // Full but scoped to a different warehouse -> ignored.
                new ScopeGrant(HierarchyLevel.Warehouse, Guid.NewGuid(), PermissionLevel.Full),
                // Read, in scope -> the only applicable one.
                new ScopeGrant(HierarchyLevel.Warehouse, scopedWarehouse, PermissionLevel.Read)
            });
        var resource = ResourceInB(warehouse: scopedWarehouse);

        var d = EffectiveAccessEvaluator.Evaluate(Actor, resource, PermissionLevel.Read, connection);

        Assert.True(d.Allowed);
        Assert.Equal(PermissionLevel.Read, d.EffectivePermission);
    }
}
