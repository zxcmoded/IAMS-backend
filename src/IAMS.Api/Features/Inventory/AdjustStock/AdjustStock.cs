using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IAMS.Api.Features.Inventory.AdjustStock;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>Signed manual adjustment against a single bin (BR-009/010). Reason required; delta non-zero.</summary>
public record AdjustStockCommand(
    string IdempotencyKey,
    Guid InventoryItemId,
    Guid BinId,
    decimal QuantityDelta,
    string Reason,
    long? BaseStockVersion,
    string? DeviceId,
    DateTime? ClientCreatedAtUtc);

public class AdjustStockValidator : AbstractValidator<AdjustStockCommand>
{
    public AdjustStockValidator()
    {
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(200);
        RuleFor(x => x.InventoryItemId).NotEmpty();
        RuleFor(x => x.BinId).NotEmpty();
        RuleFor(x => x.QuantityDelta).NotEqual(0).WithMessage("Adjustment delta must be non-zero.");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(400);
        RuleFor(x => x.DeviceId).MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class AdjustStockHandler(StockMovementService movements)
{
    public async Task<Results<Ok<StockMovementResponse>, ProblemHttpResult>> HandleAsync(
        AdjustStockCommand command, CancellationToken ct)
    {
        // An adjustment is a signed delta against the SOURCE bin (schema §2.3): SourceBin set, no destination.
        var result = await movements.ApplyAsync(new MovementRequest(
            Type: InventoryTransactionType.Adjustment,
            InventoryItemId: command.InventoryItemId,
            SourceBinId: command.BinId,
            DestinationBinId: null,
            Quantity: command.QuantityDelta,
            AdjustmentReason: command.Reason,
            IdempotencyKey: command.IdempotencyKey,
            BaseSourceStockVersion: command.BaseStockVersion,
            BaseDestinationStockVersion: null,
            DeviceId: command.DeviceId,
            ClientCreatedAtUtc: command.ClientCreatedAtUtc), ct);

        return result.Error is not null
            ? result.Error.ToProblem()
            : TypedResults.Ok(result.Success!);
    }
}
