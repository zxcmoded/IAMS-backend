using IAMS.Api.Common.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAMS.Api.Tests;

public class BadRequestExceptionHandlerTests
{
    [Fact]
    public async Task MapsBindingFailure_To400ValidationFailed_WithCode()
    {
        var handler = new BadRequestExceptionHandler();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        ctx.Response.Body = new MemoryStream();

        // Mirrors what minimal-API model binding throws for a non-GUID parentId / non-int pageSize.
        var handled = await handler.TryHandleAsync(
            ctx, new BadHttpRequestException("Failed to bind parameter.", StatusCodes.Status400BadRequest), default);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("validation_failed", body);
    }

    [Fact]
    public async Task LeavesNonBindingExceptionsToDefaultPipeline()
    {
        var handler = new BadRequestExceptionHandler();
        var ctx = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), default);

        Assert.False(handled);
    }
}
