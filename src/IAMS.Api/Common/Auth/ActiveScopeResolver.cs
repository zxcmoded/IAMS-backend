using IAMS.Api.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Auth;

/// <summary>The active scope resolved for a session: which tenant/company/location the session acts within.</summary>
public readonly record struct ActiveScope(Guid TenantId, Guid CompanyId, Guid? LocationId);

/// <summary>
/// Resolves a user's active company/tenant. Active company comes from the user's primary
/// <see cref="Domain.UserCompanyMembership"/> (a user may belong to several companies); the tenant is
/// derived from that company. Connection scope is deliberately NOT resolved here — it is evaluated live
/// per request so policy changes take effect without re-login (BR-TC-007).
/// </summary>
public class ActiveScopeResolver(IamsDbContext db)
{
    /// <summary>Resolve from the user's primary membership (used at 2FA verify, when a new session is created).</summary>
    public async Task<ActiveScope?> ResolvePrimaryAsync(Guid userId, CancellationToken ct)
    {
        var membership = await db.UserCompanyMemberships
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.IsPrimary)
            .Select(m => new { m.CompanyId, m.Company.TenantId })
            .FirstOrDefaultAsync(ct);

        return membership is null ? null : new ActiveScope(membership.TenantId, membership.CompanyId, null);
    }

    /// <summary>Resolve the tenant for an already-chosen active company (used at token refresh).</summary>
    public async Task<Guid?> ResolveTenantForCompanyAsync(Guid companyId, CancellationToken ct)
    {
        var tenantId = await db.Companies
            .Where(c => c.Id == companyId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct);
        return tenantId;
    }
}
