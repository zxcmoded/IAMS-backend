using FluentValidation;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IAMS.Api.Features.Auth.ResendTwoFactor;

// ── Contract ────────────────────────────────────────────────────────────────
public record ResendTwoFactorCommand(string ChallengeToken);

public record ResendTwoFactorResponse(
    string ChallengeToken,
    int ExpiresInSeconds,
    int ResendAvailableInSeconds,
    string? DevOtp);

public class ResendTwoFactorValidator : AbstractValidator<ResendTwoFactorCommand>
{
    public ResendTwoFactorValidator() =>
        RuleFor(x => x.ChallengeToken).NotEmpty().MaximumLength(128);
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class ResendTwoFactorHandler(
    IamsDbContext db,
    IClock clock,
    IOptions<JwtOptions> options,
    IHostEnvironment env,
    ILogger<ResendTwoFactorHandler> logger)
{
    public async Task<Results<Ok<ResendTwoFactorResponse>, ProblemHttpResult>> HandleAsync(
        ResendTwoFactorCommand command, CancellationToken ct)
    {
        var challenge = await db.OtpChallenges
            .FirstOrDefaultAsync(c => c.ChallengeToken == command.ChallengeToken, ct);

        if (challenge is null || challenge.IsConsumed)
        {
            return ApiError.Problem(StatusCodes.Status404NotFound,
                ErrorCodes.NotFound, "No active challenge for that token.");
        }

        var opt = options.Value;

        // Total resend cap: stops a held challenge token being used to spam a victim's OTP delivery.
        if (challenge.ResendCount >= opt.TwoFactorMaxResends)
        {
            return ApiError.Problem(StatusCodes.Status429TooManyRequests,
                ErrorCodes.ResendLimitReached, "Too many codes requested. Please sign in again.");
        }

        var now = clock.UtcNow;
        if (now.UtcDateTime < challenge.ResendAvailableAtUtc)
        {
            var retryIn = (int)Math.Ceiling((challenge.ResendAvailableAtUtc - now.UtcDateTime).TotalSeconds);
            return ApiError.Problem(StatusCodes.Status429TooManyRequests,
                ErrorCodes.ResendTooSoon, $"Please wait {retryIn}s before requesting a new code.");
        }
        var code = TokenGenerator.NewNumericOtp();

        // Rotate the code and extend the window; reset attempt counter for the new code.
        challenge.CodeHash = PasswordHasher.Hash(code);
        challenge.ExpiresAtUtc = now.AddSeconds(opt.TwoFactorCodeTtlSeconds).UtcDateTime;
        challenge.ResendAvailableAtUtc = now.AddSeconds(opt.TwoFactorResendCooldownSeconds).UtcDateTime;
        challenge.ResendCount++;
        challenge.AttemptCount = 0;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("2FA code resent for challenge {Challenge}: {Code}",
            challenge.ChallengeToken, code);

        var devOtp = env.IsProduction() ? null : code;
        return TypedResults.Ok(new ResendTwoFactorResponse(
            challenge.ChallengeToken,
            opt.TwoFactorCodeTtlSeconds,
            opt.TwoFactorResendCooldownSeconds,
            devOtp));
    }
}
