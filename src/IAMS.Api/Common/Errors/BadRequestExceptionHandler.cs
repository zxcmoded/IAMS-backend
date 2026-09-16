using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace IAMS.Api.Common.Errors;

/// <summary>
/// Maps parameter-binding failures to the standard ProblemDetails shape with a <c>validation_failed</c>
/// code. Minimal-API model binding throws <see cref="BadHttpRequestException"/> <b>before</b> any endpoint
/// or FluentValidation runs — e.g. a non-GUID <c>parentId</c>, an empty <c>?parentId=</c>, or a non-int
/// <c>pageSize</c> — which would otherwise surface as a raw framework 400 with no machine-readable
/// <c>code</c>, inconsistent with every other 400 in this API. Anything that isn't a binding failure is
/// left for the default pipeline (returns <c>false</c>).
/// </summary>
public sealed class BadRequestExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        var status = badRequest.StatusCode is >= 400 and < 500
            ? badRequest.StatusCode
            : StatusCodes.Status400BadRequest;

        var result = ApiError.Problem(status, ErrorCodes.ValidationFailed,
            "One or more request parameters are malformed.");
        await result.ExecuteAsync(httpContext);
        return true;
    }
}
