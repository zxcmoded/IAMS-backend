using IAMS.Api.Common;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIamsServices(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Health");
app.MapIamsEndpoints();

app.Run();

// Exposed so the integration test project (WebApplicationFactory) can reference the entry point.
public partial class Program;
