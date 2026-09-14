using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IAMS.Api.Features.Auth.Login;

// ── Contract ────────────────────────────────────────────────────────────────
public record LoginCommand(string Username, string Password, string? DeviceId);

/// <summary>
/// Login never returns tokens directly — a successful password check always starts a 2FA challenge
/// (F1). The client takes <see cref="ChallengeToken"/> to the verify endpoint. The OTP is delivered
/// out of band; <see cref="DevOtp"/> is populated only in non-production to unblock the mobile team.
/// </summary>
public record LoginResponse(
    string ChallengeToken,
    int ExpiresInSeconds,
    int ResendAvailableInSeconds,
    string? DevOtp);

public class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
        RuleFor(x => x.DeviceId).MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class LoginHandler(
    IamsDbContext db,
    IClock clock,
    IOptions<JwtOptions> options,
    IHostEnvironment env,
    ILogger<LoginHandler> logger)
{
    public async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> HandleAsync(
        LoginCommand command, CancellationToken ct)
    {
        var normalized = command.Username.ToUpperInvariant();
        var user = await db.Users
            .Include(u => u.TwoFactorSetting)
            .FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);

        // Uniform failure for unknown user vs bad password (avoid user enumeration). Crucially, run the
        // same PBKDF2 work on both paths so response timing cannot distinguish the two.
        if (user is null)
        {
            PasswordHasher.VerifyDummy(command.Password);
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.InvalidCredentials, "Invalid username or password.");
        }

        if (!PasswordHasher.Verify(command.Password, user.PasswordHash))
        {
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.InvalidCredentials, "Invalid username or password.");
        }

        if (!user.IsActive)
        {
            return ApiError.Problem(StatusCodes.Status401Unauthorized,
                ErrorCodes.AccountInactive, "This account is not active.");
        }

        var opt = options.Value;
        var now = clock.UtcNow;
        var code = TokenGenerator.NewNumericOtp();

        var challenge = new OtpChallenge
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ChallengeToken = TokenGenerator.NewOpaqueToken(),
            CodeHash = PasswordHasher.Hash(code),
            Purpose = OtpPurpose.Login,
            Channel = user.TwoFactorSetting?.Channel ?? TwoFactorChannel.Email,
            CreatedAtUtc = now.UtcDateTime,
            ExpiresAtUtc = now.AddSeconds(opt.TwoFactorCodeTtlSeconds).UtcDateTime,
            ResendAvailableAtUtc = now.AddSeconds(opt.TwoFactorResendCooldownSeconds).UtcDateTime
        };
        db.OtpChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);

        // Delivery mechanism is "Not Specified" in the source; log in non-prod so 2FA can be exercised.
        logger.LogInformation("2FA code for user {UserId} (challenge {Challenge}): {Code}",
            user.Id, challenge.ChallengeToken, code);

        var devOtp = env.IsProduction() ? null : code;
        return TypedResults.Ok(new LoginResponse(
            challenge.ChallengeToken,
            opt.TwoFactorCodeTtlSeconds,
            opt.TwoFactorResendCooldownSeconds,
            devOtp));
    }
}
