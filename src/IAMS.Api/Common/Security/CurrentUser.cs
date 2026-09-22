using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using IAMS.Api.Common.Domain;

namespace IAMS.Api.Common.Security;

/// <summary>Resolves the authenticated actor's identity and role/company from the current request's claims.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    Guid CompanyId { get; }
    UserRole Role { get; }

    /// <summary>The issuing session's id (session_id claim), used to revoke that session on logout. Null if absent.</summary>
    Guid? SessionId { get; }
}

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid UserId => GetGuid(JwtRegisteredClaimNames.Sub) ?? GetGuid(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No authenticated user id on the current request.");

    public Guid CompanyId => GetGuid(IamsClaims.CompanyId)
        ?? throw new InvalidOperationException("No company claim on the current request.");

    public UserRole Role =>
        int.TryParse(Principal?.FindFirst(IamsClaims.Role)?.Value, out var code) && Enum.IsDefined(typeof(UserRole), code)
            ? (UserRole)code
            : throw new InvalidOperationException("No valid role claim on the current request.");

    public Guid? SessionId => GetGuid(IamsClaims.SessionId);

    private Guid? GetGuid(string claimType)
    {
        var raw = Principal?.FindFirst(claimType)?.Value;
        return Guid.TryParse(raw, out var value) ? value : null;
    }
}
