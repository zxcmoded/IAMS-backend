using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.Inventory.SyncItems;
using IAMS.Api.Features.Inventory.SyncStockLevels;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Opt-in integration tests (skipped unless <c>IAMS_PG_TEST_CONN</c> is set) that exercise the two offline
/// inventory sync feeds against a REAL PostgreSQL instance. The in-memory provider cannot evaluate the
/// <c>SyncCursorUtc GENERATED ALWAYS AS COALESCE(UpdatedAtUtc, CreatedAtUtc) STORED</c> column, nor prove that
/// the <c>EF.Property&lt;DateTime&gt;("SyncCursorUtc")</c> ordering + <c>Id.CompareTo</c> tie-break translate
/// to correct SQL over <c>IX_InventoryItems_Sync</c> / <c>IX_StockLevels_Sync</c> — so those guarantees are
/// only meaningful here. Mirrors <see cref="MasterDataSyncSqlIntegrationTests"/>.
///
/// In the shared "RealPostgresIntegration" collection so it never races the other real-Postgres classes on the
/// same database name.
/// </summary>
[Collection("RealPostgresIntegration")]
public class InventorySyncSqlIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("IAMS_PG_TEST_CONN");

    private static IamsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IamsDbContext>().UseNpgsql(Conn).Options);

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(
        Guid CompanyId, IReadOnlyList<Guid> ItemIds, IReadOnlyList<Guid> StockIds);

    /// <summary>
    /// Seeds one company with inventory items and stock levels spanning: CreatedAtUtc-only rows, an
    /// UpdatedAtUtc-overriding row, and a deliberate SyncCursorUtc tie (two rows sharing the same coalesced
    /// timestamp, distinct Ids) so the Id tie-break is actually exercised. SyncCursorUtc is never set by us —
    /// Postgres generates it.
    /// </summary>
    private static async Task<Seed> SeedAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Co", CreatedAtUtc = T0 };
        var location = new Location { Id = Guid.NewGuid(), Name = "L", CompanyId = company.Id };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), Name = "W", LocationId = location.Id, CompanyId = company.Id };
        var rack = new Rack { Id = Guid.NewGuid(), Name = "R", WarehouseId = warehouse.Id, LocationId = location.Id, CompanyId = company.Id };

        Bin MakeBin(string n) => new()
        {
            Id = Guid.NewGuid(), Name = n, CompanyId = company.Id,
            LocationId = location.Id, WarehouseId = warehouse.Id, RackId = rack.Id
        };
        var binA = MakeBin("BIN-A");
        var binB = MakeBin("BIN-B");
        var binC = MakeBin("BIN-C");
        var binD = MakeBin("BIN-D");
        var binE = MakeBin("BIN-E");

        InventoryItem Item(string sku, DateTime created, DateTime? updated) => new()
        {
            Id = Guid.NewGuid(), CompanyId = company.Id,
            Sku = sku, Name = $"Item {sku}", IsActive = true, CreatedAtUtc = created, UpdatedAtUtc = updated
        };
        var i1 = Item("i1", T0.AddDays(1), null);          // cursor = created (T0+1)
        var i2 = Item("i2", T0.AddDays(2), null);          // cursor = created (T0+2)
        var i3 = Item("i3", T0.AddDays(1), T0.AddDays(5));  // cursor = updated (T0+5), overrides created
        var iTieA = Item("itieA", T0.AddDays(3), null);     // cursor = T0+3  ┐ tie
        var iTieB = Item("itieB", T0.AddDays(3), null);     // cursor = T0+3  ┘ tie, distinct Id

        var items = new[] { i1, i2, i3, iTieA, iTieB };
        var bins = new[] { binA, binB, binC, binD, binE };

        StockLevel Stock(InventoryItem item, Bin bin, decimal qty, long version, DateTime created, DateTime? updated) => new()
        {
            Id = Guid.NewGuid(), InventoryItemId = item.Id, BinId = bin.Id,
            RackId = rack.Id, WarehouseId = warehouse.Id, LocationId = location.Id,
            CompanyId = company.Id,
            QuantityOnHand = qty, Version = version, CreatedAtUtc = created, UpdatedAtUtc = updated
        };
        var s1 = Stock(i1, binA, 10m, 1, T0.AddDays(1), null);           // cursor = created
        var s2 = Stock(i2, binB, 20m, 1, T0.AddDays(2), null);           // cursor = created
        var s3 = Stock(i3, binC, 30m, 3, T0.AddDays(1), T0.AddDays(5));  // cursor = updated, overrides
        var sTieA = Stock(iTieA, binD, 40m, 1, T0.AddDays(3), null);     // cursor = T0+3 ┐ tie
        var sTieB = Stock(iTieB, binE, 50m, 1, T0.AddDays(3), null);     // cursor = T0+3 ┘ tie, distinct Id
        var stock = new[] { s1, s2, s3, sTieA, sTieB };

        db.AddRange(company, location, warehouse, rack);
        db.AddRange(bins);
        db.AddRange(items);
        db.AddRange(stock);
        await db.SaveChangesAsync();

        return new Seed(company.Id, items.Select(i => i.Id).ToList(), stock.Select(s => s.Id).ToList());
    }

    private static SyncItemsHandler ItemsHandler(IamsDbContext db, Seed seed) =>
        new(db, new AccessScopeResolver(db, new FakeCurrentUser
        {
            UserId = Guid.NewGuid(), CompanyId = seed.CompanyId, Role = UserRole.Admin
        }));

    private static SyncStockLevelsHandler StockHandler(IamsDbContext db, Seed seed) =>
        new(db, new AccessScopeResolver(db, new FakeCurrentUser
        {
            UserId = Guid.NewGuid(), CompanyId = seed.CompanyId, Role = UserRole.Admin
        }));

    [Fact]
    public async Task Items_GeneratedColumn_EqualsCoalesce_AndRegeneratesOnUpdate()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var seed = await SeedAsync();

        await using var db = NewContext();

        var i1 = seed.ItemIds[0]; // CreatedAtUtc-only → cursor == CreatedAtUtc
        var cursor1 = await db.InventoryItems.Where(i => i.Id == i1)
            .Select(i => EF.Property<DateTime>(i, SyncCursor.ColumnName)).SingleAsync();
        var created1 = await db.InventoryItems.Where(i => i.Id == i1).Select(i => i.CreatedAtUtc).SingleAsync();
        Assert.Equal(created1, cursor1);

        var i3 = seed.ItemIds[2]; // UpdatedAtUtc set → cursor == UpdatedAtUtc (COALESCE picks non-null)
        var cursor3 = await db.InventoryItems.Where(i => i.Id == i3)
            .Select(i => EF.Property<DateTime>(i, SyncCursor.ColumnName)).SingleAsync();
        Assert.Equal(T0.AddDays(5), cursor3);

        var entity = await db.InventoryItems.SingleAsync(i => i.Id == i1);
        entity.UpdatedAtUtc = T0.AddDays(9);
        await db.SaveChangesAsync();

        await using var verify = NewContext();
        var cursor1After = await verify.InventoryItems.Where(i => i.Id == i1)
            .Select(i => EF.Property<DateTime>(i, SyncCursor.ColumnName)).SingleAsync();
        Assert.Equal(T0.AddDays(9), cursor1After);
    }

    [Fact]
    public async Task Items_KeysetPagination_MatchesFullOrderedScan_StableAndTieBroken()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var seed = await SeedAsync();

        await using var db = NewContext();

        var expected = await db.InventoryItems
            .OrderBy(i => EF.Property<DateTime>(i, SyncCursor.ColumnName)).ThenBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync();
        Assert.Equal(seed.ItemIds.Count, expected.Count);

        var handler = ItemsHandler(db, seed);
        var collected = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = Assert.IsType<Ok<MasterDataPage<InventoryItemSyncDto>>>(
                (await handler.HandleAsync(new SyncItemsQuery(cursor, 2), default)).Result).Value!;
            collected.AddRange(page.Items.Select(i => i.Id));
            if (!page.HasMore)
            {
                var tail = Assert.IsType<Ok<MasterDataPage<InventoryItemSyncDto>>>(
                    (await handler.HandleAsync(new SyncItemsQuery(page.NextCursor, 2), default)).Result).Value!;
                Assert.Empty(tail.Items);
                Assert.Null(tail.NextCursor);
                break;
            }
            cursor = page.NextCursor;
        }

        Assert.Equal(expected, collected);                          // same order, no skips
        Assert.Equal(expected.Count, collected.Distinct().Count()); // no dupes across page boundaries
    }

    [Fact]
    public async Task StockLevels_KeysetPagination_MatchesFullOrderedScan_StableAndTieBroken()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var seed = await SeedAsync();

        await using var db = NewContext();

        var expected = await db.StockLevels
            .OrderBy(s => EF.Property<DateTime>(s, SyncCursor.ColumnName)).ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync();
        Assert.Equal(seed.StockIds.Count, expected.Count);

        var handler = StockHandler(db, seed);
        var collected = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = Assert.IsType<Ok<MasterDataPage<StockLevelSyncDto>>>(
                (await handler.HandleAsync(new SyncStockLevelsQuery(cursor, 2), default)).Result).Value!;
            collected.AddRange(page.Items.Select(s => s.Id));
            if (!page.HasMore)
            {
                var tail = Assert.IsType<Ok<MasterDataPage<StockLevelSyncDto>>>(
                    (await handler.HandleAsync(new SyncStockLevelsQuery(page.NextCursor, 2), default)).Result).Value!;
                Assert.Empty(tail.Items);
                Assert.Null(tail.NextCursor);
                break;
            }
            cursor = page.NextCursor;
        }

        Assert.Equal(expected, collected);
        Assert.Equal(expected.Count, collected.Distinct().Count());
    }

    [Fact]
    public async Task CompositeSyncIndexes_WereCreatedByMigration()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        await SeedAsync();

        await using var db = NewContext();
        var itemsIdx = await db.Database
            .SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_InventoryItems_Sync'")
            .AnyAsync();
        var stockIdx = await db.Database
            .SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_StockLevels_Sync'")
            .AnyAsync();
        Assert.True(itemsIdx);
        Assert.True(stockIdx);
    }
}
