namespace IAMS.Api.Common.MasterData;

/// <summary>
/// Shared response envelope for the paginated master-data listing endpoints.
/// </summary>
/// <param name="Items">The page of rows, ordered by (SyncCursorUtc, Id).</param>
/// <param name="NextCursor">
/// Opaque cursor to pass as <c>cursor</c> on the next request to continue from where this page ended.
/// <c>null</c> when the page is empty — which means "nothing new since your cursor," NOT "reset": a
/// client must never clear its stored cursor on an empty page.
/// </param>
/// <param name="HasMore">
/// True when more rows remain beyond this page (determined by a peek-ahead fetch of pageSize+1 rows,
/// so no separate COUNT query is needed).
/// </param>
public record MasterDataPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
