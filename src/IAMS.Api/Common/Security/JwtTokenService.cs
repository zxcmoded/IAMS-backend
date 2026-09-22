using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IAMS.Api.Common.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IAMS.Api.Common.Security;

/// <summary>Custom claim types embedded in the access token so the actor's identity/scope is available without a DB hit.</summary>
public static class IamsClaims
{
    public const string CompanyId = "company_id";
    public const string Role = "role";
    public const string SessionId = "session_id";
    public const string Username = "username";
}

/// <summary>The identity embedded in an access token, resolved from the user at activation time.</summary>
public readonly record struct AccessTokenScope(
    Guid UserId,
    string Username,
    Guid CompanyId,
    UserRole Role,
    Guid SessionId);

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
            new(IamsClaims.CompanyId, scope.CompanyId.ToString()),
            // The role's int code is the authoritative wire value (privilege ordering is numeric).
            new(IamsClaims.Role, ((int)scope.Role).ToString()),
            new(IamsClaims.SessionId, scope.SessionId.ToString())
        };

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
