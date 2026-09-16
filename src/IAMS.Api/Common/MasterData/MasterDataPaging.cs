namespace IAMS.Api.Common.MasterData;

/// <summary>
/// Shared paging constants and the peek-ahead page-assembly used by every master-data listing handler.
/// </summary>
public static class MasterDataPaging
{
    public const int DefaultPageSize = 200;

    /// <summary>Hard cap on <c>pageSize</c> — same precedent as the access batch endpoint's 500-item cap.</summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// Turns a peek-ahead fetch (up to <paramref name="pageSize"/>+1 rows already ordered by
    /// (SyncCursorUtc, Id)) into a <see cref="MasterDataPage{TDto}"/>: trims the extra row, sets
    /// <c>HasMore</c>, and encodes <c>NextCursor</c> from the last kept row. An empty page yields a
    /// <c>null</c> cursor — meaning "nothing new," never "reset".
    /// </summary>
    public static MasterDataPage<TDto> BuildPage<TRow, TDto>(
        IReadOnlyList<TRow> fetched,
        int pageSize,
        Func<TRow, DateTime> cursorOf,
        Func<TRow, TDto> dtoOf,
        Func<TRow, Guid> idOf)
    {
        var hasMore = fetched.Count > pageSize;
        var kept = hasMore ? fetched.Take(pageSize).ToList() : fetched.ToList();

        var nextCursor = kept.Count == 0
            ? null
            : SyncCursor.Encode(cursorOf(kept[^1]), idOf(kept[^1]));

        var items = kept.Select(dtoOf).ToList();
        return new MasterDataPage<TDto>(items, nextCursor, hasMore);
    }
}
