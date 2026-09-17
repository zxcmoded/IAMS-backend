using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Inventory;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Inventory.ListInventory;

// ── Contract ────────────────────────────────────────────────────────────────
public record InventoryListItemDto(
    Guid Id,
    string Sku,
    string? Barcode,
    string Name,
    string? UnitOfMeasure,
    string? Category,
    bool IsActive,
    decimal TotalQuantityOnHand);

public record InventoryListResponse(
    IReadOnlyList<InventoryListItemDto> Items,
    int Page,
    int PageSize,
    bool HasMore);

/// <param name="Search">Case-insensitive substring over SKU / name / barcode.</param>
/// <param name="Filter">all (default) | in_stock | low_stock | out_of_stock — over total on-hand across bins.</param>
/// <param name="LowStockThreshold">Inclusive upper bound for <c>low_stock</c>; defaults to <see cref="InventoryDefaults.DefaultLowStockThreshold"/>.</param>
public record ListInventoryQuery(
    string? Search,
    string? Filter,
    decimal? LowStockThreshold,
    int? Page,
    int? PageSize);

public static class InventoryStockFilter
{
    public const string All = "all";
    public const string InStock = "in_stock";
    public const string LowStock = "low_stock";
    public const string OutOfStock = "out_of_stock";

    public static readonly string[] Allowed = { All, InStock, LowStock, OutOfStock };
}

public class ListInventoryQueryValidator : AbstractValidator<ListInventoryQuery>
{
    public ListInventoryQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0).When(x => x.Page.HasValue);
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(ListInventoryHandler.MaxPageSize)
            .When(x => x.PageSize.HasValue);
        RuleFor(x => x.LowStockThreshold).GreaterThanOrEqualTo(0).When(x => x.LowStockThreshold.HasValue);
        RuleFor(x => x.Filter)
            .Must(f => InventoryStockFilter.Allowed.Contains(f!))
            .When(x => !string.IsNullOrEmpty(x.Filter))
            .WithMessage($"Filter must be one of: {string.Join(", ", InventoryStockFilter.Allowed)}.");
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Online inventory listing for the F4 list screen: reachable-company scoped, substring-searchable, and
/// filterable by aggregate on-hand (in-stock / low-stock / out-of-stock). Offset-paginated with a peek-ahead
/// (pageSize+1) fetch so <c>hasMore</c> needs no separate COUNT. This is distinct from the offline sync feed
/// (InventoryItems/StockLevels ride the shared SyncCursor mechanism) — it's the interactive query surface.
/// </summary>
public class ListInventoryHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<Results<Ok<InventoryListResponse>, ProblemHttpResult>> HandleAsync(
        ListInventoryQuery query, CancellationToken ct)
    {
        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;
        var filter = string.IsNullOrEmpty(query.Filter) ? InventoryStockFilter.All : query.Filter;
        var lowThreshold = query.LowStockThreshold ?? InventoryDefaults.DefaultLowStockThreshold;

        var items = db.InventoryItems.AsNoTracking().Where(i => reachable.Contains(i.CompanyId));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // ToLower().Contains translates to a case-insensitive LIKE on Npgsql and is also evaluable by the
            // in-memory test provider (unlike EF.Functions.ILike, which is Npgsql-only).
            var s = query.Search.Trim().ToLower();
            items = items.Where(i =>
                i.Sku.ToLower().Contains(s) ||
                i.Name.ToLower().Contains(s) ||
                (i.Barcode != null && i.Barcode.ToLower().Contains(s)));
        }

        // Aggregate on-hand across all of the item's stock rows (all in the item's own — reachable — company).
        var projected = items.Select(i => new
        {
            Item = i,
            Total = db.StockLevels.Where(sl => sl.InventoryItemId == i.Id)
                .Sum(sl => (decimal?)sl.QuantityOnHand) ?? 0m
        });

        projected = filter switch
        {
            InventoryStockFilter.InStock => projected.Where(x => x.Total > 0),
            InventoryStockFilter.OutOfStock => projected.Where(x => x.Total <= 0),
            InventoryStockFilter.LowStock => projected.Where(x => x.Total > 0 && x.Total <= lowThreshold),
            _ => projected
        };

        var rows = await projected
            .OrderBy(x => x.Item.Sku).ThenBy(x => x.Item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .Select(x => new InventoryListItemDto(
                x.Item.Id, x.Item.Sku, x.Item.Barcode, x.Item.Name,
                x.Item.UnitOfMeasure, x.Item.Category, x.Item.IsActive, x.Total))
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        var pageItems = hasMore ? rows.Take(pageSize).ToList() : rows;

        return TypedResults.Ok(new InventoryListResponse(pageItems, page, pageSize, hasMore));
    }
}
