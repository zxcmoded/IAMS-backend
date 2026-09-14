using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Connections;

/// <summary>
/// The intended entry point for mutating connection policy (enable/disable, permission level, scopes).
/// Every method loads the connection tracked and saves in one transaction; the actual
/// <see cref="CompanyConnection.PolicyRevision"/> bump is enforced centrally in
/// <c>IamsDbContext.SaveChanges</c>, so even a future caller that bypasses this service cannot forget it.
/// Phase 1 has no admin/web endpoint for this yet (connection config is admin/web-only per F15's open
/// gap); this service exists so that endpoint, when added, has a correct, tested mutation path.
/// </summary>
public class ConnectionPolicyService(IamsDbContext db)
{
    public async Task<CompanyConnection?> SetEnabledAsync(Guid connectionId, bool isEnabled, CancellationToken ct)
    {
        var connection = await db.CompanyConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null)
        {
            return null;
        }

        connection.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct); // PolicyRevision bumped atomically by the DbContext
        return connection;
    }

    public async Task<CompanyConnection?> SetPermissionLevelAsync(Guid connectionId, PermissionLevel level, CancellationToken ct)
    {
        var connection = await db.CompanyConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null)
        {
            return null;
        }

        connection.PermissionLevel = level;
        await db.SaveChangesAsync(ct);
        return connection;
    }

    public async Task<CompanyConnection?> ReplaceScopesAsync(
        Guid connectionId, IReadOnlyList<CompanyConnectionScope> scopes, CancellationToken ct)
    {
        // Load the connection tracked (without its Scopes navigation) so the SaveChanges override finds it
        // and bumps PolicyRevision; manage scope rows through the DbSet directly.
        var connection = await db.CompanyConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null)
        {
            return null;
        }

        var existing = await db.CompanyConnectionScopes
            .Where(s => s.CompanyConnectionId == connectionId)
            .ToListAsync(ct);
        db.CompanyConnectionScopes.RemoveRange(existing);

        foreach (var scope in scopes)
        {
            scope.Id = scope.Id == Guid.Empty ? Guid.NewGuid() : scope.Id;
            scope.CompanyConnectionId = connectionId;
            db.CompanyConnectionScopes.Add(scope);
        }

        await db.SaveChangesAsync(ct); // scope add/remove bumps PolicyRevision atomically
        return connection;
    }
}
