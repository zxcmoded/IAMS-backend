using IAMS.Api.Common.Domain;

namespace IAMS.Api.Common.Access;

/// <summary>
/// How a <see cref="UserRole"/> is presented over the wire: BOTH its authoritative int <see cref="Code"/>
/// (e.g. 700 — safe for numeric privilege comparisons on the client) and its <see cref="Name"/>
/// (e.g. "Admin" — human/debug friendly). Emitting both removes any ambiguity for clients parsing the role.
/// </summary>
public record RoleDto(int Code, string Name)
{
    public static RoleDto From(UserRole role) => new((int)role, role.ToString());
}
