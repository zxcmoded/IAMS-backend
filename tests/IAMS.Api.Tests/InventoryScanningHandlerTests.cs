using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Inventory;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.Inventory.AdjustStock;
using IAMS.Api.Features.Inventory.ApproveStockCount;
using IAMS.Api.Features.Inventory.CreateStockCount;
using IAMS.Api.Features.Inventory.GetInventoryItem;
using IAMS.Api.Features.Inventory.ListInventory;
using IAMS.Api.Features.Inventory.ReceiveStock;
using IAMS.Api.Features.Inventory.RejectStockCount;
using IAMS.Api.Features.Inventory.TransferStock;
using IAMS.Api.Features.Scanning.ResolveScan;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// In-memory handler tests for the Phase-2a F3 (scan resolve) and F4 (inventory ops) slices: identifier
/// resolution + BR-003 blocking, idempotent replay, the StockLevel.Version conflict signal, the non-negative
/// pre-check, the variance/threshold approval branch, and the reachable-company scoping. Provider-specific
/// guarantees (generated Variance/SyncCursor columns, the CHECK constraints, real unique-violation replay)
/// are proven separately in <see cref="InventorySqlIntegrationTests"/>.
/// </summary>
public class InventoryScanningHandlerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public required IamsDbContext Db { get; init; }
        public required FakeCurrentUser User { get; init; }
        public required Guid CompanyId { get; init; }
        public required Guid BinAId { get; init; }
        public required Guid BinBId { get; init; }
        public required Guid ItemId { get; init; }

        public AccessScopeResolver Access => new(Db, User);
        public StockMovementService Movements => new(Db, Access, User);
    }

    private static Fixture NewFixture(string sku = "SKU-1", string? barcode = "BC-1", string binAName = "BIN-A")
    {
        var db = TestDb.New();
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Companies.Add(new Company { Id = companyId, Name = "Co", CreatedAtUtc = T0 });

        Bin MakeBin(string name) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            RackId = Guid.NewGuid(),
            WarehouseId = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            CompanyId = companyId,
            IsActive = true,
            CreatedAtUtc = T0
        };
        var binA = MakeBin(binAName);
        var binB = MakeBin("BIN-B");
        db.Bins.AddRange(binA, binB);

        var item = new InventoryItem
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Sku = sku,
            Barcode = barcode,
            Name = "Widget",
            IsActive = true,
            CreatedAtUtc = T0
        };
        db.InventoryItems.Add(item);
        db.SaveChanges();

        return new Fixture
        {
            Db = db,
            User = new FakeCurrentUser { UserId = userId, CompanyId = companyId, Role = UserRole.Admin },
            CompanyId = companyId,
            BinAId = binA.Id,
            BinBId = binB.Id,
            ItemId = item.Id
        };
    }

    private static ResolveScanHandler Scanner(Fixture f) => new(f.Db, f.Access, f.User, new FakeClock(T0));

    private static T Ok<T>(IResult result) => Assert.IsType<Ok<T>>(result).Value!;

    private static ProblemHttpResult Problem(IResult result) => Assert.IsType<ProblemHttpResult>(result);

    // ── F3 scan resolve ──────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_MatchesSku_WritesScanEvent()
    {
        var f = NewFixture();
        var result = await Scanner(f).HandleAsync(new ResolveScanCommand("SKU-1", null, null, null), default);
        var res = Ok<ResolveScanResponse>(result.Result);

        Assert.Equal(ScanResolvedType.Sku.ToString(), res.ResolvedType);
        Assert.Equal(f.ItemId, res.ResolvedEntityId);
        Assert.Equal("Widget", res.Label);
        Assert.Equal(1, await f.Db.ScanEvents.CountAsync());
    }

    [Fact]
    public async Task Resolve_MatchesBarcode_AsSku()
    {
        var f = NewFixture();
        var res = Ok<ResolveScanResponse>((await Scanner(f).HandleAsync(
            new ResolveScanCommand("BC-1", null, null, null), default)).Result);
        Assert.Equal(ScanResolvedType.Sku.ToString(), res.ResolvedType);
        Assert.Equal(f.ItemId, res.ResolvedEntityId);
    }

    [Fact]
    public async Task Resolve_MatchesBinLabel_AsLocation()
    {
        var f = NewFixture();
        var res = Ok<ResolveScanResponse>((await Scanner(f).HandleAsync(
            new ResolveScanCommand("BIN-A", null, null, null), default)).Result);
        Assert.Equal(ScanResolvedType.Location.ToString(), res.ResolvedType);
        Assert.Equal(f.BinAId, res.ResolvedEntityId);
    }

    [Fact]
    public async Task Resolve_UnknownCode_IsNoMatch()
    {
        var f = NewFixture();
        var res = Ok<ResolveScanResponse>((await Scanner(f).HandleAsync(
            new ResolveScanCommand("does-not-exist", null, null, null), default)).Result);
        Assert.Equal(ScanResolvedType.NoMatch.ToString(), res.ResolvedType);
        Assert.Null(res.ResolvedEntityId);
    }

    [Fact]
    public async Task Resolve_CrossTenantCode_IsBlocked_AndLeaksNoId()
    {
        // Another company (NOT reachable) owns SKU "OTHER-SKU".
        var f = NewFixture();
        var otherCompany = Guid.NewGuid();
        f.Db.InventoryItems.Add(new InventoryItem
        {
            Id = Guid.NewGuid(),
            CompanyId = otherCompany,
            Sku = "OTHER-SKU",
            Name = "Secret",
            IsActive = true,
            CreatedAtUtc = T0
        });
        await f.Db.SaveChangesAsync();

        var res = Ok<ResolveScanResponse>((await Scanner(f).HandleAsync(
            new ResolveScanCommand("OTHER-SKU", null, null, null), default)).Result);

        Assert.Equal(ScanResolvedType.Blocked.ToString(), res.ResolvedType);
        Assert.Null(res.ResolvedEntityId);
        // The audit row must also carry no entity id (no cross-tenant leak).
        var logged = await f.Db.ScanEvents.SingleAsync();
        Assert.Equal(ScanResolvedType.Blocked, logged.ResolvedType);
        Assert.Null(logged.ResolvedEntityId);
    }

    [Fact]
    public async Task Resolve_IdempotentReplay_DoesNotDoubleLog()
    {
        var f = NewFixture();
        var cmd = new ResolveScanCommand("SKU-1", null, null, "scan-key-1");
        var first = Ok<ResolveScanResponse>((await Scanner(f).HandleAsync(cmd, default)).Result);
        var second = Ok<ResolveScanResponse>((await Scanner(f).HandleAsync(cmd, default)).Result);

        Assert.Equal(first.ScanEventId, second.ScanEventId);
        Assert.Equal(1, await f.Db.ScanEvents.CountAsync());
    }

    // ── F4 receive / transfer / adjust ───────────────────────────────────────

    private ReceiveStockHandler Receive(Fixture f) => new(f.Movements);
    private TransferStockHandler Transfer(Fixture f) => new(f.Movements);
    private AdjustStockHandler Adjust(Fixture f) => new(f.Movements);

    [Fact]
    public async Task Receive_CreatesStock_AndVersionStartsAtOne()
    {
        var f = NewFixture();
        var res = Ok<StockMovementResponse>((await Receive(f).HandleAsync(
            new ReceiveStockCommand("k1", f.ItemId, f.BinAId, 10m, null, null, null), default)).Result);

        Assert.False(res.Replayed);
        var state = Assert.Single(res.StockLevels);
        Assert.Equal(f.BinAId, state.BinId);
        Assert.Equal(10m, state.QuantityOnHand);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public async Task Receive_IdempotentReplay_DoesNotDoubleApply()
    {
        var f = NewFixture();
        var cmd = new ReceiveStockCommand("dup", f.ItemId, f.BinAId, 10m, null, null, null);
        await Receive(f).HandleAsync(cmd, default);
        var again = Ok<StockMovementResponse>((await Receive(f).HandleAsync(cmd, default)).Result);

        Assert.True(again.Replayed);
        Assert.Equal(10m, await f.Db.StockLevels.Where(s => s.BinId == f.BinAId).Select(s => s.QuantityOnHand).SingleAsync());
        Assert.Equal(1, await f.Db.InventoryTransactions.CountAsync());
    }

    [Fact]
    public async Task Transfer_MovesQuantity_BumpsBothVersions()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);

        var res = Ok<StockMovementResponse>((await Transfer(f).HandleAsync(
            new TransferStockCommand("t", f.ItemId, f.BinAId, f.BinBId, 4m, null, null, null, null), default)).Result);

        var a = res.StockLevels.Single(s => s.BinId == f.BinAId);
        var b = res.StockLevels.Single(s => s.BinId == f.BinBId);
        Assert.Equal(6m, a.QuantityOnHand);
        Assert.Equal(4m, b.QuantityOnHand);
        Assert.Equal(2, a.Version); // receive bumped to 1, transfer to 2
        Assert.Equal(1, b.Version);
    }

    [Fact]
    public async Task Transfer_OverSource_Returns422_InsufficientStock()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 3m, null, null, null), default);

        var problem = Problem((await Transfer(f).HandleAsync(
            new TransferStockCommand("t", f.ItemId, f.BinAId, f.BinBId, 5m, null, null, null, null), default)).Result);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.ProblemDetails.Status);
        Assert.Equal("insufficient_stock", problem.ProblemDetails.Extensions["code"]);
        // Nothing applied.
        Assert.Equal(3m, await f.Db.StockLevels.Where(s => s.BinId == f.BinAId).Select(s => s.QuantityOnHand).SingleAsync());
    }

    [Fact]
    public async Task Transfer_StaleBaseVersion_Returns409_Conflict()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        // Source version is now 1; stamp a stale base of 0.
        var problem = Problem((await Transfer(f).HandleAsync(
            new TransferStockCommand("t", f.ItemId, f.BinAId, f.BinBId, 1m, 0, null, null, null), default)).Result);

        Assert.Equal(StatusCodes.Status409Conflict, problem.ProblemDetails.Status);
        Assert.Equal("stock_version_conflict", problem.ProblemDetails.Extensions["code"]);
        Assert.True(problem.ProblemDetails.Extensions.ContainsKey("conflicts"));
    }

    [Fact]
    public async Task Adjust_NegativeDelta_ReducesStock()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        var res = Ok<StockMovementResponse>((await Adjust(f).HandleAsync(
            new AdjustStockCommand("a", f.ItemId, f.BinAId, -3m, "Damaged", null, null, null), default)).Result);
        Assert.Equal(7m, res.StockLevels.Single().QuantityOnHand);
    }

    [Fact]
    public async Task Adjust_OverDecrement_Returns422()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 2m, null, null, null), default);
        var problem = Problem((await Adjust(f).HandleAsync(
            new AdjustStockCommand("a", f.ItemId, f.BinAId, -5m, "Loss", null, null, null), default)).Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.ProblemDetails.Status);
        Assert.Equal("insufficient_stock", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task Receive_UnknownItem_Returns404()
    {
        var f = NewFixture();
        var problem = Problem((await Receive(f).HandleAsync(
            new ReceiveStockCommand("k", Guid.NewGuid(), f.BinAId, 1m, null, null, null), default)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.ProblemDetails.Status);
        Assert.Equal("not_found", problem.ProblemDetails.Extensions["code"]);
    }

    // ── F4 stock count ───────────────────────────────────────────────────────

    private CreateStockCountHandler Count(Fixture f) => new(f.Db, f.Access, f.User);

    [Fact]
    public async Task Count_WithinThreshold_AutoApplies_AndWritesReconcilingAdjustment()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        // Company settings: absolute threshold of 5 ⇒ a variance of 2 is within.
        f.Db.InventorySettings.Add(new InventorySettings
        {
            Id = Guid.NewGuid(), CompanyId = f.CompanyId,
            VarianceThreshold = 5m, VarianceThresholdType = VarianceThresholdType.AbsoluteQuantity, CreatedAtUtc = T0
        });
        await f.Db.SaveChangesAsync();

        var res = Ok<StockCountResponse>((await Count(f).HandleAsync(
            new CreateStockCountCommand("c1", f.ItemId, f.BinAId, 12m, null, null, null), default)).Result);

        Assert.Equal(StockCountStatus.Completed.ToString(), res.Status);
        Assert.Equal(2m, res.Variance);
        Assert.NotNull(res.AdjustmentTransactionId);
        Assert.Equal(12m, await f.Db.StockLevels.Where(s => s.BinId == f.BinAId).Select(s => s.QuantityOnHand).SingleAsync());
    }

    [Fact]
    public async Task Count_OverThreshold_ParksPendingApproval_NoStockChange()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        f.Db.InventorySettings.Add(new InventorySettings
        {
            Id = Guid.NewGuid(), CompanyId = f.CompanyId,
            VarianceThreshold = 1m, VarianceThresholdType = VarianceThresholdType.AbsoluteQuantity, CreatedAtUtc = T0
        });
        await f.Db.SaveChangesAsync();

        var res = Ok<StockCountResponse>((await Count(f).HandleAsync(
            new CreateStockCountCommand("c1", f.ItemId, f.BinAId, 20m, null, null, null), default)).Result);

        Assert.Equal(StockCountStatus.PendingApproval.ToString(), res.Status);
        Assert.Null(res.AdjustmentTransactionId);
        Assert.Equal(10m, await f.Db.StockLevels.Where(s => s.BinId == f.BinAId).Select(s => s.QuantityOnHand).SingleAsync());
    }

    [Fact]
    public async Task Count_NoSettingsRow_UsesFallbackZeroThreshold_NonZeroVarianceNeedsApproval()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        // No InventorySettings row ⇒ fallback threshold 0 ⇒ any non-zero variance ⇒ PendingApproval.
        var res = Ok<StockCountResponse>((await Count(f).HandleAsync(
            new CreateStockCountCommand("c1", f.ItemId, f.BinAId, 11m, null, null, null), default)).Result);
        Assert.Equal(StockCountStatus.PendingApproval.ToString(), res.Status);
        Assert.Equal(0m, res.VarianceThreshold);
    }

    [Fact]
    public async Task ApproveCount_AppliesReconcilingAdjustment()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        var created = Ok<StockCountResponse>((await Count(f).HandleAsync(
            new CreateStockCountCommand("c1", f.ItemId, f.BinAId, 25m, null, null, null), default)).Result);
        Assert.Equal(StockCountStatus.PendingApproval.ToString(), created.Status);

        var approve = new ApproveStockCountHandler(f.Db, f.Access, f.User, new FakeClock(T0));
        var res = Ok<StockCountResponse>((await approve.HandleAsync(created.Id, default)).Result);

        Assert.Equal(StockCountStatus.Approved.ToString(), res.Status);
        Assert.NotNull(res.AdjustmentTransactionId);
        Assert.Equal(25m, await f.Db.StockLevels.Where(s => s.BinId == f.BinAId).Select(s => s.QuantityOnHand).SingleAsync());
    }

    [Fact]
    public async Task RejectCount_NoStockChange()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        var created = Ok<StockCountResponse>((await Count(f).HandleAsync(
            new CreateStockCountCommand("c1", f.ItemId, f.BinAId, 25m, null, null, null), default)).Result);

        var reject = new RejectStockCountHandler(f.Db, f.Access, new FakeClock(T0));
        var res = Ok<StockCountResponse>((await reject.HandleAsync(created.Id, new RejectStockCountCommand("miscount"), default)).Result);

        Assert.Equal(StockCountStatus.Rejected.ToString(), res.Status);
        Assert.Equal(10m, await f.Db.StockLevels.Where(s => s.BinId == f.BinAId).Select(s => s.QuantityOnHand).SingleAsync());
    }

    [Fact]
    public async Task ApproveCount_NotPending_Returns409()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 10m, null, null, null), default);
        f.Db.InventorySettings.Add(new InventorySettings
        {
            Id = Guid.NewGuid(), CompanyId = f.CompanyId,
            VarianceThreshold = 100m, VarianceThresholdType = VarianceThresholdType.AbsoluteQuantity, CreatedAtUtc = T0
        });
        await f.Db.SaveChangesAsync();
        var created = Ok<StockCountResponse>((await Count(f).HandleAsync(
            new CreateStockCountCommand("c1", f.ItemId, f.BinAId, 12m, null, null, null), default)).Result);
        Assert.Equal(StockCountStatus.Completed.ToString(), created.Status); // auto-applied, not pending

        var approve = new ApproveStockCountHandler(f.Db, f.Access, f.User, new FakeClock(T0));
        var problem = Problem((await approve.HandleAsync(created.Id, default)).Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.ProblemDetails.Status);
        Assert.Equal("stock_count_not_pending", problem.ProblemDetails.Extensions["code"]);
    }

    // ── F4 list + detail ─────────────────────────────────────────────────────

    [Fact]
    public async Task List_FilterInStock_ExcludesZeroOnHand()
    {
        var f = NewFixture();
        // A second item with no stock.
        f.Db.InventoryItems.Add(new InventoryItem
        {
            Id = Guid.NewGuid(), CompanyId = f.CompanyId,
            Sku = "SKU-2", Name = "Empty", IsActive = true, CreatedAtUtc = T0
        });
        await f.Db.SaveChangesAsync();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 5m, null, null, null), default);

        var handler = new ListInventoryHandler(f.Db, f.Access);
        var res = Ok<InventoryListResponse>((await handler.HandleAsync(
            new ListInventoryQuery(null, "in_stock", null, null, null), default)).Result);

        var only = Assert.Single(res.Items);
        Assert.Equal("SKU-1", only.Sku);
        Assert.Equal(5m, only.TotalQuantityOnHand);
    }

    [Fact]
    public async Task List_FilterLowStock_UsesThreshold()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 3m, null, null, null), default);
        var handler = new ListInventoryHandler(f.Db, f.Access);

        var low = Ok<InventoryListResponse>((await handler.HandleAsync(
            new ListInventoryQuery(null, "low_stock", 5m, null, null), default)).Result);
        Assert.Single(low.Items); // 3 <= 5

        var lowTight = Ok<InventoryListResponse>((await handler.HandleAsync(
            new ListInventoryQuery(null, "low_stock", 2m, null, null), default)).Result);
        Assert.Empty(lowTight.Items); // 3 > 2
    }

    [Fact]
    public async Task Detail_ReturnsStockAndMovements_404WhenNotReachable()
    {
        var f = NewFixture();
        await Receive(f).HandleAsync(new ReceiveStockCommand("r", f.ItemId, f.BinAId, 8m, null, null, null), default);
        var handler = new GetInventoryItemHandler(f.Db, f.Access);

        var res = Ok<InventoryItemDetailResponse>((await handler.HandleAsync(f.ItemId, default)).Result);
        Assert.Equal(8m, res.TotalQuantityOnHand);
        Assert.Single(res.StockByBin);
        Assert.Single(res.Movements);

        var problem = Problem((await handler.HandleAsync(Guid.NewGuid(), default)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.ProblemDetails.Status);
    }
}
