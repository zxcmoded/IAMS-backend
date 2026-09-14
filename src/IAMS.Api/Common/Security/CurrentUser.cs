using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace IAMS.Api.Common.Security;

/// <summary>Resolves the authenticated actor's identity and active scope from the current request's claims.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    Guid TenantId { get; }
    Guid CompanyId { get; }
    Guid? LocationId { get; }
    bool IsSystemAdmin { get; }
}

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid UserId => GetGuid(JwtRegisteredClaimNames.Sub) ?? GetGuid(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No authenticated user id on the current request.");

    public Guid TenantId => GetGuid(IamsClaims.TenantId)
        ?? throw new InvalidOperationException("No tenant claim on the current request.");

    public Guid CompanyId => GetGuid(IamsClaims.CompanyId)
        ?? throw new InvalidOperationException("No company claim on the current request.");

    public Guid? LocationId => GetGuid(IamsClaims.LocationId);

    public bool IsSystemAdmin => Principal?.FindFirst(IamsClaims.IsSystemAdmin)?.Value == "true";

    private Guid? GetGuid(string claimType)
    {
        var raw = Principal?.FindFirst(claimType)?.Value;
        return Guid.TryParse(raw, out var value) ? value : null;
    }
}
