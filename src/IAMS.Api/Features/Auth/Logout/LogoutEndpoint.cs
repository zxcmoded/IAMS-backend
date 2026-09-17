namespace IAMS.Api.Features.Auth.Logout;

public static class LogoutEndpoint
{
    public static void MapLogoutEndpoint(this IEndpointRouteBuilder app)
    {
        // No request body: the session to revoke is taken from the caller's own bearer token (session_id
        // claim). Revocation is an audit record only — see LogoutHandler.
        app.MapPost("/api/auth/logout", async Task<IResult> (
            LogoutHandler handler,
            CancellationToken ct) => await handler.HandleAsync(ct))
        .RequireAuthorization()
        .WithName("Logout")
        .WithTags("Auth");
    }
}
