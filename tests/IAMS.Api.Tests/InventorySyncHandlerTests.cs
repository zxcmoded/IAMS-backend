using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.Inventory.SyncItems;
using IAMS.Api.Features.Inventory.SyncStockLevels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// In-memory handler tests for the two offline inventory sync feeds (<c>GET /api/inventory/sync/items</c> and
/// <c>GET /api/inventory/sync/stock-levels</c>): reachable-company scoping, peek-ahead keyset pagination with
/// no skip/dupe across pages, incremental cursor resume, and malformed-cursor → 400. Same technique as
/// <see cref="MasterDataSyncHandlerTests"/>: the stored generated <c>SyncCursorUtc</c> column can't be
/// evaluated by the in-memory provider, so the fixture seeds its shadow value explicitly; the real Postgres
/// generated-column + index ordering is proven in <see cref="InventorySyncSqlIntegrationTests"/>.
/// </summary>
public class InventorySyncHandlerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public required IamsDbContext Db { get; init; }
        public required FakeCurrentUser User { get; init; }
        public required Guid HomeCompanyId { get; init; }
        public required Guid TenantId { get; init; }
    }

    private static Fixture NewFixture()
    {
        var db = TestDb.New();
        var tenantId = Guid.NewGuid();
        var home = new Company { Id = Guid.NewGuid(), Name = "Home", TenantId = tenantId, CreatedAtUtc = T0 };
        db.Add(home);
        db.SaveChanges();
        return new Fixture
        {
            Db = db,
            User = new FakeCurrentUser { UserId = Guid.NewGuid(), TenantId = tenantId, CompanyId = home.Id },
            HomeCompanyId = home.Id,
            TenantId = tenantId
        };
    }

    private static void SetCursor(IamsDbContext db, object entity, DateTime cursor)
        => db.Entry(entity).Property(SyncCursor.ColumnName).CurrentValue = cursor;

    private static void Connect(IamsDbContext db, Guid source, Guid target, bool enabled)
        => db.Add(new CompanyConnection
        {
            Id = Guid.NewGuid(),
            SourceCompanyId = source,
            TargetCompanyId = target,
            ConnectionType = ConnectionType.ParentToChild,
            IsEnabled = enabled,
            PermissionLevel = PermissionLevel.Read,
            PolicyRevision = 1
        });

    private static Company AddCompany(IamsDbContext db, string name, DateTime created)
    {
        var c = new Company { Id = Guid.NewGuid(), Name = name, TenantId = Guid.NewGuid(), CreatedAtUtc = created };
        db.Add(c);
        return c;
    }

    private static InventoryItem AddItem(
        IamsDbContext db, Guid companyId, Guid tenantId, string sku, DateTime created,
        DateTime? updated = null, bool active = true, string? barcode = null)
    {
        var item = new InventoryItem
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            TenantId = tenantId,
            Sku = sku,
            Barcode = barcode,
            Name = $"Item {sku}",
            Description = $"Desc {sku}",
            UnitOfMeasure = "EA",
            Category = "CatA",
            IsActive = active,
            CreatedAtUtc = created,
            UpdatedAtUtc = updated
        };
        db.Add(item);
        SetCursor(db, item, updated ?? created);
        return item;
    }

    private static StockLevel AddStock(
        IamsDbContext db, Guid itemId, Guid companyId, Guid tenantId, decimal qty, long version,
        DateTime created, DateTime? updated = null)
    {
        var sl = new StockLevel
        {
            Id = Guid.NewGuid(),
            InventoryItemId = itemId,
            BinId = Guid.NewGuid(),
            RackId = Guid.NewGuid(),
            WarehouseId = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            CompanyId = companyId,
            TenantId = tenantId,
            QuantityOnHand = qty,
            Version = version,
            CreatedAtUtc = created,
            UpdatedAtUtc = updated
        };
        db.Add(sl);
        SetCursor(db, sl, updated ?? created);
        return sl;
    }

    private static SyncItemsHandler ItemsHandler(Fixture f) => new(f.Db, new AccessCheckService(f.Db, f.User));
    private static SyncStockLevelsHandler StockHandler(Fixture f) => new(f.Db, new AccessCheckService(f.Db, f.User));

    private static async Task<MasterDataPage<InventoryItemSyncDto>> RunItems(Fixture f, SyncItemsQuery q)
        => Assert.IsType<Ok<MasterDataPage<InventoryItemSyncDto>>>((await ItemsHandler(f).HandleAsync(q, default)).Result).Value!;

    private static async Task<MasterDataPage<StockLevelSyncDto>> RunStock(Fixture f, SyncStockLevelsQuery q)
        => Assert.IsType<Ok<MasterDataPage<StockLevelSyncDto>>>((await StockHandler(f).HandleAsync(q, default)).Result).Value!;

    // ── Reachable-company scoping ─────────────────────────────────────────────

    [Fact]
    public async Task Items_AreFilteredByReachableCompany()
    {
        var f = NewFixture();
        var enabled = AddCompany(f.Db, "Enabled", T0.AddDays(1));
        var disabled = AddCompany(f.Db, "Disabled", T0.AddDays(2));
        var unconnected = AddCompany(f.Db, "Unconnected", T0.AddDays(3));
        Connect(f.Db, f.HomeCompanyId, enabled.Id, enabled: true);
        Connect(f.Db, f.HomeCompanyId, disabled.Id, enabled: false);
        var homeItem = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "HOME-1", T0.AddDays(4));
        var enabledItem = AddItem(f.Db, enabled.Id, enabled.TenantId, "EN-1", T0.AddDays(5));
        var disabledItem = AddItem(f.Db, disabled.Id, disabled.TenantId, "DIS-1", T0.AddDays(6));
        var unconnectedItem = AddItem(f.Db, unconnected.Id, unconnected.TenantId, "UN-1", T0.AddDays(7));
        await f.Db.SaveChangesAsync();

        var ids = (await RunItems(f, new SyncItemsQuery(null, null))).Items.Select(i => i.Id).ToHashSet();

        Assert.Contains(homeItem.Id, ids);
        Assert.Contains(enabledItem.Id, ids);
        Assert.DoesNotContain(disabledItem.Id, ids);
        Assert.DoesNotContain(unconnectedItem.Id, ids);
    }

    [Fact]
    public async Task StockLevels_AreFilteredByReachableCompany()
    {
        var f = NewFixture();
        var enabled = AddCompany(f.Db, "Enabled", T0.AddDays(1));
        var disabled = AddCompany(f.Db, "Disabled", T0.AddDays(2));
        Connect(f.Db, f.HomeCompanyId, enabled.Id, enabled: true);
        Connect(f.Db, f.HomeCompanyId, disabled.Id, enabled: false);
        var homeItem = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "HOME-1", T0.AddDays(3));
        var enabledItem = AddItem(f.Db, enabled.Id, enabled.TenantId, "EN-1", T0.AddDays(4));
        var disabledItem = AddItem(f.Db, disabled.Id, disabled.TenantId, "DIS-1", T0.AddDays(5));
        var homeStock = AddStock(f.Db, homeItem.Id, f.HomeCompanyId, f.TenantId, 10m, 1, T0.AddDays(6));
        var enabledStock = AddStock(f.Db, enabledItem.Id, enabled.Id, enabled.TenantId, 20m, 1, T0.AddDays(7));
        var disabledStock = AddStock(f.Db, disabledItem.Id, disabled.Id, disabled.TenantId, 30m, 1, T0.AddDays(8));
        await f.Db.SaveChangesAsync();

        var ids = (await RunStock(f, new SyncStockLevelsQuery(null, null))).Items.Select(s => s.Id).ToHashSet();

        Assert.Contains(homeStock.Id, ids);
        Assert.Contains(enabledStock.Id, ids);
        Assert.DoesNotContain(disabledStock.Id, ids);
    }

    // ── DTO field integrity ───────────────────────────────────────────────────

    [Fact]
    public async Task Items_Dto_CarriesEveryOfflineField_IncludingInactive()
    {
        var f = NewFixture();
        var item = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "SKU-1", T0.AddDays(1), updated: T0.AddDays(2),
            active: false, barcode: "BC-1");
        await f.Db.SaveChangesAsync();

        var dto = Assert.Single((await RunItems(f, new SyncItemsQuery(null, null))).Items);

        Assert.Equal(item.Id, dto.Id);
        Assert.Equal(f.TenantId, dto.TenantId);
        Assert.Equal(f.HomeCompanyId, dto.CompanyId);
        Assert.Equal("SKU-1", dto.Sku);
        Assert.Equal("BC-1", dto.Barcode);
        Assert.Equal("Item SKU-1", dto.Name);
        Assert.Equal("Desc SKU-1", dto.Description);
        Assert.Equal("EA", dto.UnitOfMeasure);
        Assert.Equal("CatA", dto.Category);
        Assert.False(dto.IsActive); // soft-deleted rows must still sync so the client can converge deletions
        Assert.Equal(T0.AddDays(1), dto.CreatedAtUtc);
        Assert.Equal(T0.AddDays(2), dto.UpdatedAtUtc);
    }

    [Fact]
    public async Task StockLevels_Dto_CarriesAncestryAndVersion()
    {
        var f = NewFixture();
        var item = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "SKU-1", T0.AddDays(1));
        var sl = AddStock(f.Db, item.Id, f.HomeCompanyId, f.TenantId, 42m, version: 7, created: T0.AddDays(2), updated: T0.AddDays(3));
        await f.Db.SaveChangesAsync();

        var dto = Assert.Single((await RunStock(f, new SyncStockLevelsQuery(null, null))).Items);

        Assert.Equal(sl.Id, dto.Id);
        Assert.Equal(item.Id, dto.InventoryItemId);
        Assert.Equal(sl.BinId, dto.BinId);
        Assert.Equal(sl.RackId, dto.RackId);
        Assert.Equal(sl.WarehouseId, dto.WarehouseId);
        Assert.Equal(sl.LocationId, dto.LocationId);
        Assert.Equal(f.HomeCompanyId, dto.CompanyId);
        Assert.Equal(f.TenantId, dto.TenantId);
        Assert.Equal(42m, dto.QuantityOnHand);
        Assert.Equal(7, dto.Version);
        Assert.Equal(T0.AddDays(2), dto.CreatedAtUtc);
        Assert.Equal(T0.AddDays(3), dto.UpdatedAtUtc);
    }

    // ── Peek-ahead pagination boundaries ──────────────────────────────────────

    [Fact]
    public async Task Items_PageSizeExactlyMatchesRowCount_HasMoreFalse_ButCursorPresent()
    {
        var f = NewFixture();
        for (var i = 1; i <= 5; i++)
        {
            AddItem(f.Db, f.HomeCompanyId, f.TenantId, $"SKU-{i}", T0.AddDays(i));
        }
        await f.Db.SaveChangesAsync();

        var page = await RunItems(f, new SyncItemsQuery(null, 5));

        Assert.Equal(5, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.NotNull(page.NextCursor); // non-empty page → resumable cursor even when nothing more remains
    }

    [Fact]
    public async Task Items_MoreRowsThanPageSize_PagesThroughAllWithoutOverlap()
    {
        var f = NewFixture();
        for (var i = 1; i <= 5; i++)
        {
            AddItem(f.Db, f.HomeCompanyId, f.TenantId, $"SKU-{i}", T0.AddDays(i));
        }
        await f.Db.SaveChangesAsync();

        var page1 = await RunItems(f, new SyncItemsQuery(null, 2));
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasMore);

        var page2 = await RunItems(f, new SyncItemsQuery(page1.NextCursor, 2));
        Assert.Equal(2, page2.Items.Count);
        Assert.True(page2.HasMore);

        var page3 = await RunItems(f, new SyncItemsQuery(page2.NextCursor, 2));
        Assert.Single(page3.Items);
        Assert.False(page3.HasMore);

        var all = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(i => i.Id).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count()); // no skip, no dupe across pages
    }

    [Fact]
    public async Task StockLevels_MoreRowsThanPageSize_PagesThroughAllWithoutOverlap()
    {
        var f = NewFixture();
        var item = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "SKU-1", T0.AddDays(1));
        for (var i = 1; i <= 5; i++)
        {
            AddStock(f.Db, item.Id, f.HomeCompanyId, f.TenantId, i * 10m, i, T0.AddDays(10 + i));
        }
        await f.Db.SaveChangesAsync();

        var page1 = await RunStock(f, new SyncStockLevelsQuery(null, 2));
        var page2 = await RunStock(f, new SyncStockLevelsQuery(page1.NextCursor, 2));
        var page3 = await RunStock(f, new SyncStockLevelsQuery(page2.NextCursor, 2));

        Assert.True(page1.HasMore);
        Assert.True(page2.HasMore);
        Assert.False(page3.HasMore);

        var all = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(s => s.Id).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count());
    }

    // ── Empty page semantics ──────────────────────────────────────────────────

    [Fact]
    public async Task Items_EmptyPage_ReturnsNullCursor_MeansUnchanged_NotReset()
    {
        var f = NewFixture();
        AddItem(f.Db, f.HomeCompanyId, f.TenantId, "SKU-1", T0.AddDays(1));
        await f.Db.SaveChangesAsync();

        var first = await RunItems(f, new SyncItemsQuery(null, 50));
        Assert.Single(first.Items);

        var second = await RunItems(f, new SyncItemsQuery(first.NextCursor, 50));
        Assert.Empty(second.Items);
        Assert.Null(second.NextCursor);
        Assert.False(second.HasMore);
    }

    // ── Incremental cursor resume ─────────────────────────────────────────────

    [Fact]
    public async Task Items_IncrementalCursor_ReturnsOnlyRowsAfterCursor_AcrossCreatedAndUpdated()
    {
        var f = NewFixture();
        var createdOnly = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "CREATED", created: T0.AddDays(1));
        var updatedLater = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "UPDATED", created: T0.AddDays(1), updated: T0.AddDays(10));
        await f.Db.SaveChangesAsync();

        var cursor = SyncCursor.Encode(T0.AddDays(1), createdOnly.Id);
        var page = await RunItems(f, new SyncItemsQuery(cursor, null));

        Assert.Contains(updatedLater.Id, page.Items.Select(i => i.Id));
        Assert.DoesNotContain(createdOnly.Id, page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task StockLevels_IncrementalCursor_SurfacesRebumpedRow()
    {
        var f = NewFixture();
        var item = AddItem(f.Db, f.HomeCompanyId, f.TenantId, "SKU-1", T0.AddDays(1));
        var stable = AddStock(f.Db, item.Id, f.HomeCompanyId, f.TenantId, 10m, 1, created: T0.AddDays(2));
        var bumped = AddStock(f.Db, item.Id, f.HomeCompanyId, f.TenantId, 20m, 5, created: T0.AddDays(2), updated: T0.AddDays(9));
        await f.Db.SaveChangesAsync();

        var cursor = SyncCursor.Encode(T0.AddDays(2), stable.Id);
        var page = await RunStock(f, new SyncStockLevelsQuery(cursor, null));

        Assert.Contains(bumped.Id, page.Items.Select(s => s.Id));
        Assert.DoesNotContain(stable.Id, page.Items.Select(s => s.Id));
    }

    // ── Malformed cursor → 400 validation_failed ──────────────────────────────
    // Guards the exact bug the master-data-sync feature shipped: SyncCursor.TryDecode's false return being
    // discarded, silently serving a start-of-world page. Both new handlers must map it to 400.

    private const string MalformedCursor = "not-a-valid-cursor!!!";

    private static void AssertMalformedCursor(IResult actual)
    {
        var problem = Assert.IsType<ProblemHttpResult>(actual);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("validation_failed", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task Items_MalformedCursor_Returns400ValidationFailed()
    {
        var f = NewFixture();
        var result = await ItemsHandler(f).HandleAsync(new SyncItemsQuery(MalformedCursor, null), default);
        AssertMalformedCursor(result.Result);
    }

    [Fact]
    public async Task StockLevels_MalformedCursor_Returns400ValidationFailed()
    {
        var f = NewFixture();
        var result = await StockHandler(f).HandleAsync(new SyncStockLevelsQuery(MalformedCursor, null), default);
        AssertMalformedCursor(result.Result);
    }
}
