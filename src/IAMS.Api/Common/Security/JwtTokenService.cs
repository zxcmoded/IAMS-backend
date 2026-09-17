using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IAMS.Api.Common.Security;

/// <summary>Custom claim types embedded in the access token so the actor's active scope is available without a DB hit.</summary>
public static class IamsClaims
{
    public const string TenantId = "tenant_id";
    public const string CompanyId = "company_id";
    public const string LocationId = "location_id";
    public const string SessionId = "session_id";
    public const string Username = "username";

    /// <summary>Platform-admin flag (User.IsSystemAdmin); only present (as "true") when set, so ordinary tokens stay minimal.</summary>
    public const string IsSystemAdmin = "is_system_admin";
}

/// <summary>The resolved active scope embedded in an access token, resolved from the user's session at login.</summary>
public readonly record struct AccessTokenScope(
    Guid UserId,
    string Username,
    Guid TenantId,
    Guid ActiveCompanyId,
    Guid? ActiveLocationId,
    Guid SessionId,
    bool IsSystemAdmin = false);

public class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AccessTokenScope scope, DateTimeOffset now)
    {
        var expires = now.AddDays(_options.AccessTokenLifetimeDays);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, scope.UserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(IamsClaims.Username, scope.Username),
            new(IamsClaims.TenantId, scope.TenantId.ToString()),
            new(IamsClaims.CompanyId, scope.ActiveCompanyId.ToString()),
            new(IamsClaims.SessionId, scope.SessionId.ToString())
        };
        if (scope.ActiveLocationId is { } locationId)
        {
            claims.Add(new Claim(IamsClaims.LocationId, locationId.ToString()));
        }
        if (scope.IsSystemAdmin)
        {
            claims.Add(new Claim(IamsClaims.IsSystemAdmin, "true"));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
