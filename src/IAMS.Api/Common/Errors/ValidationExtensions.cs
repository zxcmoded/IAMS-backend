using FluentValidation;
using FluentValidation.Results;

namespace IAMS.Api.Common.Errors;

public static class ValidationExtensions
{
    /// <summary>
    /// Validates <paramref name="instance"/>; on failure returns the errors grouped by property in the
    /// shape <see cref="ApiError.Validation"/> expects. Returns null when valid.
    /// </summary>
    public static async Task<IDictionary<string, string[]>?> ValidateToDictionaryAsync<T>(
        this IValidator<T> validator, T instance, CancellationToken ct)
    {
        ValidationResult result = await validator.ValidateAsync(instance, ct);
        if (result.IsValid)
        {
            return null;
        }

        return result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
    }
}
