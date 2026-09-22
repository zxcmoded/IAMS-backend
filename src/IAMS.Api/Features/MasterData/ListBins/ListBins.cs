using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.MasterData.ListBins;

// ── Contract ────────────────────────────────────────────────────────────────
public record BinDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    Guid RackId,
    Guid WarehouseId,
    Guid LocationId,
    Guid CompanyId);

/// <summary><paramref name="ParentId"/>, when supplied, is the parent <b>Rack</b> id to scope to.</summary>
public record ListBinsQuery(Guid? ParentId, string? Cursor, int? PageSize);

public class ListBinsQueryValidator : AbstractValidator<ListBinsQuery>
{
    public ListBinsQueryValidator()
    {
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(MasterDataPaging.MaxPageSize)
            .When(x => x.PageSize.HasValue);
        RuleFor(x => x.ParentId).NotEmpty().When(x => x.ParentId.HasValue);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class ListBinsHandler(IamsDbContext db, AccessScopeResolver scopeResolver)
{
    public async Task<Results<Ok<MasterDataPage<BinDto>>, ProblemHttpResult>> HandleAsync(
        ListBinsQuery query, CancellationToken ct)
    {
        var scope = await scopeResolver.ResolveAsync(ct);
        var locationIds = scope.LocationIds;
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        // Parent (Rack) ownership: 404 if it doesn't exist, 403 if it isn't in the caller's scope.
        if (query.ParentId is Guid pid)
        {
            var parent = await db.Racks.AsNoTracking()
                .Where(r => r.Id == pid).Select(r => new { r.CompanyId, r.LocationId }).FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Parent rack not found.");
            }
            if (!scope.LocationInScope(parent.CompanyId, parent.LocationId))
            {
                return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.AccessDenied, "Parent rack is not in your scope.");
            }
        }

        var q = db.Bins.AsNoTracking();
        if (!scope.SystemWide)
        {
            q = q.Where(x => x.CompanyId == scope.CompanyId);
        }
        if (scope.LocationRestricted)
        {
            q = q.Where(x => locationIds.Contains(x.LocationId));
        }
        if (query.ParentId is Guid parentId)
        {
            q = q.Where(x => x.RackId == parentId);
        }

        var rows = await q
            .Where(x => EF.Property<DateTime>(x, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(x, SyncCursor.ColumnName) == ts && x.Id.CompareTo(id) > 0))
            .OrderBy(x => EF.Property<DateTime>(x, SyncCursor.ColumnName)).ThenBy(x => x.Id)
            .Take(take + 1)
            .Select(x => new
            {
                Cursor = EF.Property<DateTime>(x, SyncCursor.ColumnName),
                Dto = new BinDto(x.Id, x.Name, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, x.RackId, x.WarehouseId, x.LocationId, x.CompanyId)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
