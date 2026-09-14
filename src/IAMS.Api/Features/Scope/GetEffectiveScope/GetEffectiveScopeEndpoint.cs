namespace IAMS.Api.Features.Scope.GetEffectiveScope;

public static class GetEffectiveScopeEndpoint
{
    public static void MapGetEffectiveScopeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me/scope", async Task<IResult> (
            GetEffectiveScopeHandler handler,
            CancellationToken ct) => await handler.HandleAsync(ct))
        .RequireAuthorization()
        .WithName("GetEffectiveScope")
        .WithTags("Scope");
    }
}
