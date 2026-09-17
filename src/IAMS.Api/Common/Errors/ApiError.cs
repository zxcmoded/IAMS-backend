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
    /// A stock mutation was built on a <see cref="Domain.StockLevel.Version"/> that no longer matches the
    /// server's current value — a concurrent write changed the bin while the (typically offline-queued)
    /// mutation was in flight. The 409 body carries the current version(s) so the client can rebase and retry.
    /// This is the offline-commit-then-server-conflict signal (phase-2a §2.2).
    /// </summary>
    public const string StockVersionConflict = "stock_version_conflict";

    /// <summary>
    /// A movement would drive a bin's on-hand below zero (an over-source transfer or an over-decrement
    /// adjustment). Rejected with a clean 4xx before the DB CHECK (<c>CK_StockLevels_NonNegative</c>) fires.
    /// </summary>
    public const string InsufficientStock = "insufficient_stock";

    /// <summary>
    /// An approve/reject was attempted on a stock count that is not in <c>PendingApproval</c> (already
    /// approved, rejected, or auto-completed) — the approval action is a no-op / invalid from that state.
    /// </summary>
    public const string StockCountNotPending = "stock_count_not_pending";

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

    /// <summary>
    /// Same as <see cref="Problem(int, string, string, string?)"/> but attaches extra machine-readable
    /// extension members alongside <c>code</c> — used by the stock-version conflict (409) to return the
    /// current version/quantity per affected bin so the mobile client can rebase without a second round-trip.
    /// </summary>
    public static ProblemHttpResult Problem(
        int status, string code, string title, string? detail, IReadOnlyDictionary<string, object?> extra)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        foreach (var (key, value) in extra)
        {
            extensions[key] = value;
        }

        return TypedResults.Problem(title: title, detail: detail, statusCode: status, extensions: extensions);
    }

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "One or more validation errors occurred.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });
}
