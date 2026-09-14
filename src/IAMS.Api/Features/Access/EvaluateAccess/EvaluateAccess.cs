using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IAMS.Api.Features.Access.EvaluateAccess;

// ── Contract ────────────────────────────────────────────────────────────────
public record EvaluateAccessCommand(ResourceRef Resource, PermissionLevel RequiredPermission);

public class EvaluateAccessValidator : AbstractValidator<EvaluateAccessCommand>
{
    public EvaluateAccessValidator()
    {
        RuleFor(x => x.Resource).NotNull();
        RuleFor(x => x.Resource.CompanyId).NotEmpty().When(x => x.Resource is not null);
        RuleFor(x => x.RequiredPermission)
            .Must(p => p is PermissionLevel.Read or PermissionLevel.Write or PermissionLevel.Full)
            .WithMessage("requiredPermission must be Read, Write, or Full.");
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class EvaluateAccessHandler(AccessCheckService accessCheck)
{
    public async Task<Ok<AccessDecisionResponse>> HandleAsync(
        EvaluateAccessCommand command, CancellationToken ct)
    {
        var r = command.Resource;
        var request = new AccessCheckService.Request(
            r.CompanyId, r.LocationId, r.WarehouseId, r.RackId, r.BinId, command.RequiredPermission);

        var decisions = await accessCheck.EvaluateAsync(new[] { request }, ct);
        return TypedResults.Ok(AccessDecisionResponse.From(decisions[0]));
    }
}
