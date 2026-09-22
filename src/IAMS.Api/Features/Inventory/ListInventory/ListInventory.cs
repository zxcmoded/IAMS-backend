using FluentValidation;
using IAMS.Api.Common.Access;
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
/// Online inventory listing for the F4 list screen: Company-scoped SKU catalog, substring-searchable, and
/// filterable by aggregate on-hand (in-stock / low-stock / out-of-stock). The aggregate on-hand is summed
/// over stock rows narrowed to the caller's assigned Locations for the location-restricted roles, so a
/// Viewer/Scanner sees each SKU with the on-hand that exists in their own Locations only (and the stock
/// filters apply to that scoped total). Offset-paginated with a peek-ahead (pageSize+1) fetch so
/// <c>hasMore</c> needs no separate COUNT. Distinct from the offline sync feed — this is the interactive
/// query surface.
/// </summary>
public class ListInventoryHandler(IamsDbContext db, AccessScopeResolver scopeResolver)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<Results<Ok<InventoryListResponse>, ProblemHttpResult>> HandleAsync(
        ListInventoryQuery query, CancellationToken ct)
    {
        var scope = await scopeResolver.ResolveAsync(ct);
        var locationIds = scope.LocationIds;
        var locationRestricted = scope.LocationRestricted;
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;
        var filter = string.IsNullOrEmpty(query.Filter) ? InventoryStockFilter.All : query.Filter;
        var lowThreshold = query.LowStockThreshold ?? InventoryDefaults.DefaultLowStockThreshold;

        var items = db.InventoryItems.AsNoTracking();
        if (!scope.SystemWide)
        {
            items = items.Where(i => i.CompanyId == scope.CompanyId);
        }

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

        // Aggregate on-hand across the item's stock rows, narrowed to the caller's assigned Locations for the
        // location-restricted roles (Admin/SuperAdmin sum every Location).
        var projected = items.Select(i => new
        {
            Item = i,
            Total = db.StockLevels
                .Where(sl => sl.InventoryItemId == i.Id && (!locationRestricted || locationIds.Contains(sl.LocationId)))
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
