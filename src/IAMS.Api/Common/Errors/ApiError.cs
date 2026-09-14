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
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountInactive = "account_inactive";
    public const string TwoFactorRequired = "two_factor_required";
    public const string TwoFactorInvalid = "two_factor_invalid";
    public const string TwoFactorExpired = "two_factor_expired";
    public const string TwoFactorLocked = "two_factor_locked";
    public const string ResendTooSoon = "resend_too_soon";
    public const string ResendLimitReached = "resend_limit_reached";
    public const string SessionExpired = "session_expired";
    public const string AccessDenied = "access_denied";
    public const string NoActiveCompany = "no_active_company";
    public const string NotFound = "not_found";

    /// <summary>2FA succeeded but this account is already bound to a different device (single-device access).</summary>
    public const string DeviceMismatch = "device_already_registered";
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
