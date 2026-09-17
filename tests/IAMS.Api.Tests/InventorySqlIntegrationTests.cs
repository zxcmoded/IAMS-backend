using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.Inventory.ReceiveStock;
using IAMS.Api.Features.Inventory.TransferStock;
using IAMS.Api.Common.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Opt-in integration tests (skipped unless <c>IAMS_PG_TEST_CONN</c> is set) that prove the Phase-2a schema
/// guarantees the in-memory provider cannot: the <c>CK_StockLevels_NonNegative</c> and per-type
/// <c>CK_InvTxn_TypeShape</c> CHECKs actually reject bad rows in Postgres, the unique <c>IdempotencyKey</c>
/// index de-dupes a replayed insert, the <c>Variance</c> STORED generated column computes on the server, and a
/// stale <c>Base*StockVersion</c> is detectable end-to-end. Mirrors the §7 verification checklist.
///
/// In the shared "RealPostgresIntegration" collection so it never races the other real-Postgres classes on the
/// same database name.
/// </summary>
[Collection("RealPostgresIntegration")]
public class InventorySqlIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("IAMS_PG_TEST_CONN");

    private static IamsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IamsDbContext>().UseNpgsql(Conn).Options);

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(Guid TenantId, Guid CompanyId, Guid UserId, Guid ItemId, Guid BinAId, Guid BinBId);

    private static async Task<Seed> SeedAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Kind = TenantKind.Parent };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Co", CreatedAtUtc = T0 };
        var location = new Location { Id = Guid.NewGuid(), Name = "L", TenantId = tenant.Id, CompanyId = company.Id };
        var warehouse = new Warehouse { Id = Guid.NewGuid(), Name = "W", TenantId = tenant.Id, LocationId = location.Id, CompanyId = company.Id };
        var rack = new Rack { Id = Guid.NewGuid(), Name = "R", TenantId = tenant.Id, WarehouseId = warehouse.Id, LocationId = location.Id, CompanyId = company.Id };

        Bin MakeBin(string n) => new()
        {
            Id = Guid.NewGuid(), Name = n, TenantId = tenant.Id, CompanyId = company.Id,
            LocationId = location.Id, WarehouseId = warehouse.Id, RackId = rack.Id
        };
        var binA = MakeBin("BIN-A");
        var binB = MakeBin("BIN-B");

        var user = new User
        {
            Id = Guid.NewGuid(), Username = "op", Email = "u@x.io",
            ActivationKeyHash = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAtUtc = T0
        };
        var item = new InventoryItem
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, CompanyId = company.Id,
            Sku = "SKU-1", Name = "Widget", IsActive = true, CreatedAtUtc = T0
        };

        db.AddRange(tenant, company, location, warehouse, rack, binA, binB, user, item);
        await db.SaveChangesAsync();

        return new Seed(tenant.Id, company.Id, user.Id, item.Id, binA.Id, binB.Id);
    }

    private static (StockMovementService movements, FakeCurrentUser user) Movements(IamsDbContext db, Seed s)
    {
        var user = new FakeCurrentUser { UserId = s.UserId, TenantId = s.TenantId, CompanyId = s.CompanyId };
        return (new StockMovementService(db, new AccessCheckService(db, user), user), user);
    }

    [Fact]
    public async Task NonNegativeCheck_BlocksDirectNegativeOnHand()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var s = await SeedAsync();

        await using var db = NewContext();
        db.StockLevels.Add(new StockLevel
        {
            Id = Guid.NewGuid(), InventoryItemId = s.ItemId, BinId = s.BinAId,
            RackId = Guid.NewGuid(), WarehouseId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
            CompanyId = s.CompanyId, TenantId = s.TenantId, QuantityOnHand = -1m, Version = 1
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_StockLevels_NonNegative", ex.InnerException!.Message);
    }

    [Fact]
    public async Task TypeShapeCheck_BlocksMalformedLedgerRow()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var s = await SeedAsync();

        await using var db = NewContext();
        // A Receive with a SourceBin set violates CK_InvTxn_TypeShape (Receive must have NULL source).
        db.InventoryTransactions.Add(new InventoryTransaction
        {
            Id = Guid.NewGuid(), TenantId = s.TenantId, CompanyId = s.CompanyId, InventoryItemId = s.ItemId,
            TransactionType = InventoryTransactionType.Receive,
            SourceBinId = s.BinAId, DestinationBinId = s.BinBId, Quantity = 1m,
            IdempotencyKey = "bad-shape", CreatedByUserId = s.UserId
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_InvTxn_TypeShape", ex.InnerException!.Message);
    }

    [Fact]
    public async Task IdempotencyKey_UniqueIndex_DeDupesReplayedInsert()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var s = await SeedAsync();

        // First receive via the handler.
        await using (var db = NewContext())
        {
            var (m, _) = Movements(db, s);
            await new ReceiveStockHandler(m).HandleAsync(
                new ReceiveStockCommand("same-key", s.ItemId, s.BinAId, 10m, null, null, null), default);
        }

        // Replaying the SAME key must not double-apply — handler returns the original, marked replayed.
        await using (var db = NewContext())
        {
            var (m, _) = Movements(db, s);
            var result = await new ReceiveStockHandler(m).HandleAsync(
                new ReceiveStockCommand("same-key", s.ItemId, s.BinAId, 10m, null, null, null), default);
            var res = Assert.IsType<Ok<StockMovementResponse>>(result.Result).Value!;
            Assert.True(res.Replayed);
        }

        await using (var verify = NewContext())
        {
            Assert.Equal(1, await verify.InventoryTransactions.CountAsync());
            Assert.Equal(10m, await verify.StockLevels.Where(x => x.BinId == s.BinAId)
                .Select(x => x.QuantityOnHand).SingleAsync());
        }
    }

    [Fact]
    public async Task StaleBaseVersion_IsDetected_AsConflict()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var s = await SeedAsync();

        await using (var db = NewContext())
        {
            var (m, _) = Movements(db, s);
            await new ReceiveStockHandler(m).HandleAsync(
                new ReceiveStockCommand("r", s.ItemId, s.BinAId, 10m, null, null, null), default);
        }

        // Source version is now 1; a transfer stamped with base version 0 must be a 409 conflict.
        await using (var db = NewContext())
        {
            var (m, _) = Movements(db, s);
            var result = await new TransferStockHandler(m).HandleAsync(
                new TransferStockCommand("t", s.ItemId, s.BinAId, s.BinBId, 1m, 0, null, null, null), default);
            var problem = Assert.IsType<ProblemHttpResult>(result.Result);
            Assert.Equal(409, problem.ProblemDetails.Status);
            Assert.Equal("stock_version_conflict", problem.ProblemDetails.Extensions["code"]);
        }
    }

    [Fact]
    public async Task VarianceGeneratedColumn_ComputesOnServer()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var s = await SeedAsync();

        var countId = Guid.NewGuid();
        await using (var db = NewContext())
        {
            db.StockCounts.Add(new StockCount
            {
                Id = countId, TenantId = s.TenantId, CompanyId = s.CompanyId, InventoryItemId = s.ItemId, BinId = s.BinAId,
                RackId = Guid.NewGuid(), WarehouseId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
                CountedQuantity = 15m, SystemQuantity = 10m,
                VarianceThreshold = 0m, VarianceThresholdType = VarianceThresholdType.AbsoluteQuantity,
                Status = StockCountStatus.PendingApproval, CountedByUserId = s.UserId, IdempotencyKey = "vc"
                // Variance intentionally NOT set — Postgres must generate it.
            });
            await db.SaveChangesAsync();
        }

        await using (var verify = NewContext())
        {
            var variance = await verify.StockCounts.AsNoTracking()
                .Where(c => c.Id == countId).Select(c => c.Variance).SingleAsync();
            Assert.Equal(5m, variance); // 15 - 10, computed by the STORED generated column
        }
    }
}
