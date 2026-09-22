using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Security;
using Microsoft.AspNetCore.Authorization;

namespace IAMS.Api.Common.Access;

/// <summary>
/// Named authorization policies gating endpoints by minimum <see cref="UserRole"/>. The role's int code is
/// carried in the JWT <c>role</c> claim; each policy asserts <c>role &gt;= minimum</c> using the enum's
/// numeric ordering (Viewer 100 &lt; User 200 &lt; Manager 300 &lt; Admin 700 &lt; SuperAdmin 800). This is
/// coarse privilege gating; per-request Company/Location scoping is enforced separately in handlers via
/// <see cref="AccessScopeResolver"/>.
/// </summary>
public static class Policies
{
    /// <summary>Any authenticated user with a valid role (Viewer and above) — read access.</summary>
    public const string Read = "read";

    /// <summary>User/Scanner and above — perform inventory/scanning write operations.</summary>
    public const string Write = "write";

    /// <summary>Manager and above — approve/reject stock counts and other location-management actions.</summary>
    public const string ManageInventory = "manage_inventory";

    /// <summary>Admin and above — manage company-level data and users (e.g. Location assignment, activation reset).</summary>
    public const string ManageCompany = "manage_company";

    public static void AddIamsAuthorization(this AuthorizationOptions options)
    {
        options.AddPolicy(Read, p => p.RequireAssertion(c => HasMinimumRole(c, UserRole.Viewer)));
        options.AddPolicy(Write, p => p.RequireAssertion(c => HasMinimumRole(c, UserRole.User)));
        options.AddPolicy(ManageInventory, p => p.RequireAssertion(c => HasMinimumRole(c, UserRole.Manager)));
        options.AddPolicy(ManageCompany, p => p.RequireAssertion(c => HasMinimumRole(c, UserRole.Admin)));
    }

    private static bool HasMinimumRole(AuthorizationHandlerContext context, UserRole minimum)
    {
        var raw = context.User.FindFirst(IamsClaims.Role)?.Value;
        return int.TryParse(raw, out var code) && code >= (int)minimum;
    }
}
