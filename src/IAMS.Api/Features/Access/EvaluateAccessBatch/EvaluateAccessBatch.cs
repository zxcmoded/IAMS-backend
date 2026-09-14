using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IAMS.Api.Features.Access.EvaluateAccessBatch;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>One item to evaluate. <see cref="ClientRef"/> lets the client correlate each decision to a queued op.</summary>
public record EvaluateAccessBatchItem(string? ClientRef, ResourceRef Resource, PermissionLevel RequiredPermission);

public record EvaluateAccessBatchCommand(IReadOnlyList<EvaluateAccessBatchItem> Items);

public record EvaluateAccessBatchResultItem(string? ClientRef, AccessDecisionResponse Decision);

/// <summary>
/// Batch result. <see cref="PolicyVersion"/> is the actor's aggregate connection policy version at
/// evaluation time — the client stores it as the version its queue was validated against (BR-TC-008).
/// </summary>
public record EvaluateAccessBatchResponse(
    long PolicyVersion,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<EvaluateAccessBatchResultItem> Results);

public class EvaluateAccessBatchValidator : AbstractValidator<EvaluateAccessBatchCommand>
{
    public EvaluateAccessBatchValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Resource).NotNull();
            item.RuleFor(i => i.Resource.CompanyId).NotEmpty().When(i => i.Resource is not null);
            item.RuleFor(i => i.RequiredPermission)
                .Must(p => p is PermissionLevel.Read or PermissionLevel.Write or PermissionLevel.Full)
                .WithMessage("requiredPermission must be Read, Write, or Full.");
        });
        RuleFor(x => x.Items.Count).LessThanOrEqualTo(500)
            .WithMessage("A batch may contain at most 500 items.");
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class EvaluateAccessBatchHandler(AccessCheckService accessCheck, IClock clock)
{
    public async Task<Ok<EvaluateAccessBatchResponse>> HandleAsync(
        EvaluateAccessBatchCommand command, CancellationToken ct)
    {
        var requests = command.Items.Select(i =>
        {
            var r = i.Resource;
            return new AccessCheckService.Request(
                r.CompanyId, r.LocationId, r.WarehouseId, r.RackId, r.BinId, i.RequiredPermission);
        }).ToList();

        var decisions = await accessCheck.EvaluateAsync(requests, ct);
        var policyVersion = await accessCheck.GetActorPolicyVersionAsync(ct);

        var results = command.Items
            .Zip(decisions, (item, decision) =>
                new EvaluateAccessBatchResultItem(item.ClientRef, AccessDecisionResponse.From(decision)))
            .ToList();

        return TypedResults.Ok(new EvaluateAccessBatchResponse(policyVersion, clock.UtcNow, results));
    }
}
