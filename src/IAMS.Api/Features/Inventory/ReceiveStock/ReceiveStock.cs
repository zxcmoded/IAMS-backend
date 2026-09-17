using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IAMS.Api.Features.Inventory.ReceiveStock;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>Receive stock into a bin (BR-013). No source bin; positive quantity only.</summary>
public record ReceiveStockCommand(
    string IdempotencyKey,
    Guid InventoryItemId,
    Guid DestinationBinId,
    decimal Quantity,
    long? BaseDestinationStockVersion,
    string? DeviceId,
    DateTime? ClientCreatedAtUtc);

public class ReceiveStockValidator : AbstractValidator<ReceiveStockCommand>
{
    public ReceiveStockValidator()
    {
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(200);
        RuleFor(x => x.InventoryItemId).NotEmpty();
        RuleFor(x => x.DestinationBinId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.DeviceId).MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class ReceiveStockHandler(StockMovementService movements)
{
    public async Task<Results<Ok<StockMovementResponse>, ProblemHttpResult>> HandleAsync(
        ReceiveStockCommand command, CancellationToken ct)
    {
        var result = await movements.ApplyAsync(new MovementRequest(
            Type: InventoryTransactionType.Receive,
            InventoryItemId: command.InventoryItemId,
            SourceBinId: null,
            DestinationBinId: command.DestinationBinId,
            Quantity: command.Quantity,
            AdjustmentReason: null,
            IdempotencyKey: command.IdempotencyKey,
            BaseSourceStockVersion: null,
            BaseDestinationStockVersion: command.BaseDestinationStockVersion,
            DeviceId: command.DeviceId,
            ClientCreatedAtUtc: command.ClientCreatedAtUtc), ct);

        return result.Error is not null
            ? result.Error.ToProblem()
            : TypedResults.Ok(result.Success!);
    }
}
