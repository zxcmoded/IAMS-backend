using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IAMS.Api.Features.Inventory.TransferStock;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>Move stock between two distinct bins (BR-013). Positive quantity; source must have enough on-hand.</summary>
public record TransferStockCommand(
    string IdempotencyKey,
    Guid InventoryItemId,
    Guid SourceBinId,
    Guid DestinationBinId,
    decimal Quantity,
    long? BaseSourceStockVersion,
    long? BaseDestinationStockVersion,
    string? DeviceId,
    DateTime? ClientCreatedAtUtc);

public class TransferStockValidator : AbstractValidator<TransferStockCommand>
{
    public TransferStockValidator()
    {
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(200);
        RuleFor(x => x.InventoryItemId).NotEmpty();
        RuleFor(x => x.SourceBinId).NotEmpty();
        RuleFor(x => x.DestinationBinId).NotEmpty();
        RuleFor(x => x.DestinationBinId).NotEqual(x => x.SourceBinId)
            .WithMessage("Source and destination bins must differ.");
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.DeviceId).MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class TransferStockHandler(StockMovementService movements)
{
    public async Task<Results<Ok<StockMovementResponse>, ProblemHttpResult>> HandleAsync(
        TransferStockCommand command, CancellationToken ct)
    {
        var result = await movements.ApplyAsync(new MovementRequest(
            Type: InventoryTransactionType.Transfer,
            InventoryItemId: command.InventoryItemId,
            SourceBinId: command.SourceBinId,
            DestinationBinId: command.DestinationBinId,
            Quantity: command.Quantity,
            AdjustmentReason: null,
            IdempotencyKey: command.IdempotencyKey,
            BaseSourceStockVersion: command.BaseSourceStockVersion,
            BaseDestinationStockVersion: command.BaseDestinationStockVersion,
            DeviceId: command.DeviceId,
            ClientCreatedAtUtc: command.ClientCreatedAtUtc), ct);

        return result.Error is not null
            ? result.Error.ToProblem()
            : TypedResults.Ok(result.Success!);
    }
}
