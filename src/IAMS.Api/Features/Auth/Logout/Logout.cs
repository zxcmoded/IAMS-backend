using FluentValidation;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Auth.Logout;

// ── Contract ────────────────────────────────────────────────────────────────
public record LogoutCommand(string RefreshToken);

public class LogoutValidator : AbstractValidator<LogoutCommand>
{
    public LogoutValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class LogoutHandler(IamsDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<NoContent> HandleAsync(LogoutCommand command, CancellationToken ct)
    {
        var hash = TokenGenerator.Sha256(command.RefreshToken);
        var userId = currentUser.UserId;

        // Revoke only if the session belongs to the caller; stay idempotent (always 204).
        var session = await db.UserSessions
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash && s.UserId == userId, ct);

        if (session is not null && session.RevokedAtUtc is null)
        {
            session.RevokedAtUtc = clock.UtcNow.UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.NoContent();
    }
}
