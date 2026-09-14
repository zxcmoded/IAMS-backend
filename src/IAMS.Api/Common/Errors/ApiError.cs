using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IAMS.Api.Common.Errors;

/// <summary>
/// Stable, machine-readable error codes returned in the ProblemDetails <c>code</c> extension so the
/// mobile client can branch on them (e.g. route "session_expired" to the F1 Session Expired screen)
/// without string-matching human-readable messages.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string SessionExpired = "session_expired";
    public const string AccessDenied = "access_denied";
    public const string NoActiveCompany = "no_active_company";
    public const string NotFound = "not_found";

    /// <summary>
    /// The presented Activation Key does not resolve to any active user. Deliberately the SAME code for
    /// "no such key" and "key belongs to an inactive user" — distinguishing those two would leak account
    /// state to an unauthenticated caller holding an arbitrary string.
    /// </summary>
    public const string ActivationKeyInvalid = "activation_key_invalid";

    /// <summary>The Activation Key is valid but already bound to a device other than the one presented.</summary>
    public const string ActivationKeyAlreadyBound = "activation_key_already_bound";
}

/// <summary>Factory helpers producing a consistent ProblemDetails shape with a machine-readable code.</summary>
public static class ApiError
{
    public static ProblemHttpResult Problem(int status, string code, string title, string? detail = null) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "One or more validation errors occurred.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });
}
