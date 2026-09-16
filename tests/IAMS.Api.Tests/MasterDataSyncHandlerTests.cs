using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.MasterData.ListBins;
using IAMS.Api.Features.MasterData.ListCompanies;
using IAMS.Api.Features.MasterData.ListLocations;
using IAMS.Api.Features.MasterData.ListRacks;
using IAMS.Api.Features.MasterData.ListWarehouses;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// In-memory handler tests for the five master-data listing slices: reachable-company filtering,
/// peek-ahead keyset pagination boundaries, incremental cursor filtering, inactive-row delivery, and
/// parent-ownership 403/404. The stored generated <c>SyncCursorUtc</c> column can't be evaluated by the
/// in-memory provider, so the fixture seeds its shadow value explicitly (the real Postgres computation +
/// index/ordering is proven separately in <see cref="MasterDataSyncSqlIntegrationTests"/>).
/// </summary>
public class MasterDataSyncHandlerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public required IamsDbContext Db { get; init; }
        public required FakeCurrentUser User { get; init; }
        public required Guid HomeCompanyId { get; init; }
    }

    private static Fixture NewFixture()
    {
        var db = TestDb.New();
        var home = new Company { Id = Guid.NewGuid(), Name = "Home", TenantId = Guid.NewGuid(), CreatedAtUtc = T0 };
        db.Add(home);
        SetCursor(db, home, T0);
        db.SaveChanges();
        return new Fixture
        {
            Db = db,
            User = new FakeCurrentUser { UserId = Guid.NewGuid(), TenantId = home.TenantId, CompanyId = home.Id },
            HomeCompanyId = home.Id
        };
    }

    private static void SetCursor(IamsDbContext db, object entity, DateTime cursor)
        => db.Entry(entity).Property(SyncCursor.ColumnName).CurrentValue = cursor;

    private static Company AddCompany(IamsDbContext db, string name, DateTime created, DateTime? updated = null, bool active = true)
    {
        var c = new Company { Id = Guid.NewGuid(), Name = name, IsActive = active, CreatedAtUtc = created, UpdatedAtUtc = updated, TenantId = Guid.NewGuid() };
        db.Add(c);
        SetCursor(db, c, updated ?? created);
        return c;
    }

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

    private static Location AddLocation(IamsDbContext db, Guid companyId, string name, DateTime created, DateTime? updated = null, bool active = true)
    {
        var l = new Location { Id = Guid.NewGuid(), CompanyId = companyId, Name = name, IsActive = active, CreatedAtUtc = created, UpdatedAtUtc = updated, TenantId = Guid.NewGuid() };
        db.Add(l);
        SetCursor(db, l, updated ?? created);
        return l;
    }

    private static ListCompaniesHandler CompaniesHandler(Fixture f) => new(f.Db, new AccessCheckService(f.Db, f.User));
    private static ListLocationsHandler LocationsHandler(Fixture f) => new(f.Db, new AccessCheckService(f.Db, f.User));

    /// <summary>Runs the companies handler and unwraps the success page (asserting it wasn't a problem result).</summary>
    private static async Task<MasterDataPage<CompanyDto>> RunCompanies(Fixture f, ListCompaniesQuery q)
        => Assert.IsType<Ok<MasterDataPage<CompanyDto>>>((await CompaniesHandler(f).HandleAsync(q, default)).Result).Value!;

    // ── Reachable-company filtering ───────────────────────────────────────────

    [Fact]
    public async Task Companies_ReturnsHome_PlusEnabledConnectionTarget_ExcludesDisabledAndUnconnected()
    {
        var f = NewFixture();
        var enabledTarget = AddCompany(f.Db, "Enabled", T0.AddDays(1));
        var disabledTarget = AddCompany(f.Db, "Disabled", T0.AddDays(2));
        var unconnected = AddCompany(f.Db, "Unconnected", T0.AddDays(3));
        Connect(f.Db, f.HomeCompanyId, enabledTarget.Id, enabled: true);
        Connect(f.Db, f.HomeCompanyId, disabledTarget.Id, enabled: false);
        await f.Db.SaveChangesAsync();

        var page = await RunCompanies(f, new ListCompaniesQuery(null, null));

        var ids = page.Items.Select(i => i.Id).ToHashSet();
        Assert.Equal(new[] { f.HomeCompanyId, enabledTarget.Id }.ToHashSet(), ids);
        Assert.DoesNotContain(disabledTarget.Id, ids);
        Assert.DoesNotContain(unconnected.Id, ids);
    }

    [Fact]
    public async Task Locations_AreFilteredByReachableCompany()
    {
        var f = NewFixture();
        var enabledTarget = AddCompany(f.Db, "Enabled", T0.AddDays(1));
        var disabledTarget = AddCompany(f.Db, "Disabled", T0.AddDays(2));
        Connect(f.Db, f.HomeCompanyId, enabledTarget.Id, enabled: true);
        Connect(f.Db, f.HomeCompanyId, disabledTarget.Id, enabled: false);
        var homeLoc = AddLocation(f.Db, f.HomeCompanyId, "HomeLoc", T0.AddDays(3));
        var enabledLoc = AddLocation(f.Db, enabledTarget.Id, "EnabledLoc", T0.AddDays(4));
        var disabledLoc = AddLocation(f.Db, disabledTarget.Id, "DisabledLoc", T0.AddDays(5));
        await f.Db.SaveChangesAsync();

        var result = await LocationsHandler(f).HandleAsync(new ListLocationsQuery(null, null, null), default);
        var ok = Assert.IsType<Ok<MasterDataPage<LocationDto>>>(result.Result);

        var ids = ok.Value!.Items.Select(i => i.Id).ToHashSet();
        Assert.Contains(homeLoc.Id, ids);
        Assert.Contains(enabledLoc.Id, ids);
        Assert.DoesNotContain(disabledLoc.Id, ids);
    }

    // ── Peek-ahead pagination boundaries ──────────────────────────────────────

    [Fact]
    public async Task Companies_PageSizeExactlyMatchesRowCount_HasMoreFalse()
    {
        var f = NewFixture(); // home is 1 reachable company
        for (var i = 1; i <= 4; i++)
        {
            var c = AddCompany(f.Db, $"C{i}", T0.AddDays(i));
            Connect(f.Db, f.HomeCompanyId, c.Id, enabled: true);
        }
        await f.Db.SaveChangesAsync(); // 5 reachable companies total (home + 4)

        var page = await RunCompanies(f, new ListCompaniesQuery(null, 5));

        Assert.Equal(5, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.NotNull(page.NextCursor); // non-empty page → a resumable cursor, even though nothing more remains
    }

    [Fact]
    public async Task Companies_MoreRowsThanPageSize_PagesThroughAllWithoutOverlap()
    {
        var f = NewFixture();
        for (var i = 1; i <= 4; i++)
        {
            var c = AddCompany(f.Db, $"C{i}", T0.AddDays(i));
            Connect(f.Db, f.HomeCompanyId, c.Id, enabled: true);
        }
        await f.Db.SaveChangesAsync(); // 5 reachable companies

        var page1 = await RunCompanies(f, new ListCompaniesQuery(null, 2));
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasMore);

        var page2 = await RunCompanies(f, new ListCompaniesQuery(page1.NextCursor, 2));
        Assert.Equal(2, page2.Items.Count);
        Assert.True(page2.HasMore);

        var page3 = await RunCompanies(f, new ListCompaniesQuery(page2.NextCursor, 2));
        Assert.Single(page3.Items);
        Assert.False(page3.HasMore);

        var all = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(i => i.Id).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count()); // no overlap across pages
    }

    [Fact]
    public async Task Companies_EmptyPage_ReturnsNullCursor_Documented_MeansUnchanged()
    {
        var f = NewFixture(); // only home
        var first = await RunCompanies(f, new ListCompaniesQuery(null, 50));
        Assert.Single(first.Items);

        // Re-poll from the tail cursor: nothing new → empty page, null cursor (client must NOT clear its cursor).
        var second = await RunCompanies(f, new ListCompaniesQuery(first.NextCursor, 50));
        Assert.Empty(second.Items);
        Assert.Null(second.NextCursor);
        Assert.False(second.HasMore);
    }

    // ── Incremental cursor filtering (CreatedAtUtc-only vs UpdatedAtUtc-also) ─

    [Fact]
    public async Task Companies_IncrementalCursor_ReturnsOnlyRowsAfterCursor_AcrossCreatedAndUpdated()
    {
        var f = NewFixture();
        // createdOnly cursor = created; updated cursor = its UpdatedAtUtc (later than created).
        var createdOnly = AddCompany(f.Db, "CreatedOnly", created: T0.AddDays(1));
        var updatedLater = AddCompany(f.Db, "UpdatedLater", created: T0.AddDays(1), updated: T0.AddDays(10));
        Connect(f.Db, f.HomeCompanyId, createdOnly.Id, enabled: true);
        Connect(f.Db, f.HomeCompanyId, updatedLater.Id, enabled: true);
        await f.Db.SaveChangesAsync();

        // Cursor positioned just after createdOnly (T0+1 day, its Id) — should surface only updatedLater (T0+10).
        var cursor = SyncCursor.Encode(T0.AddDays(1), createdOnly.Id);
        var page = await RunCompanies(f, new ListCompaniesQuery(cursor, null));

        // home (T0) and createdOnly (T0+1) are at/behind the cursor; updatedLater (T0+10) is ahead.
        Assert.Contains(updatedLater.Id, page.Items.Select(i => i.Id));
        Assert.DoesNotContain(createdOnly.Id, page.Items.Select(i => i.Id));
        Assert.DoesNotContain(f.HomeCompanyId, page.Items.Select(i => i.Id));
    }

    // ── Inactive rows still delivered ─────────────────────────────────────────

    [Fact]
    public async Task Companies_InactiveRowsStillReturnedInPage()
    {
        var f = NewFixture();
        var inactive = AddCompany(f.Db, "SoftDeleted", T0.AddDays(1), active: false);
        Connect(f.Db, f.HomeCompanyId, inactive.Id, enabled: true);
        await f.Db.SaveChangesAsync();

        var page = await RunCompanies(f, new ListCompaniesQuery(null, null));

        var row = Assert.Single(page.Items, i => i.Id == inactive.Id);
        Assert.False(row.IsActive);
    }

    // ── Parent-ownership 403 / 404 ────────────────────────────────────────────

    [Fact]
    public async Task Locations_ParentCompanyNotFound_Returns404NotFound()
    {
        var f = NewFixture();
        var result = await LocationsHandler(f).HandleAsync(new ListLocationsQuery(Guid.NewGuid(), null, null), default);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("not_found", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task Locations_ParentCompanyUnreachable_Returns403AccessDenied()
    {
        var f = NewFixture();
        var unreachable = AddCompany(f.Db, "Unreachable", T0.AddDays(1)); // exists but no enabled connection
        await f.Db.SaveChangesAsync();

        var result = await LocationsHandler(f).HandleAsync(new ListLocationsQuery(unreachable.Id, null, null), default);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal("access_denied", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task Locations_ParentCompanyReachable_ScopesToThatCompanyOnly()
    {
        var f = NewFixture();
        var other = AddCompany(f.Db, "Other", T0.AddDays(1));
        Connect(f.Db, f.HomeCompanyId, other.Id, enabled: true);
        var homeLoc = AddLocation(f.Db, f.HomeCompanyId, "HomeLoc", T0.AddDays(2));
        var otherLoc = AddLocation(f.Db, other.Id, "OtherLoc", T0.AddDays(3));
        await f.Db.SaveChangesAsync();

        var result = await LocationsHandler(f).HandleAsync(new ListLocationsQuery(f.HomeCompanyId, null, null), default);
        var ok = Assert.IsType<Ok<MasterDataPage<LocationDto>>>(result.Result);

        var row = Assert.Single(ok.Value!.Items);
        Assert.Equal(homeLoc.Id, row.Id);
        Assert.DoesNotContain(otherLoc.Id, ok.Value.Items.Select(i => i.Id));
    }

    // ── Child-level parent resolution uses the correct parent table/column ────

    [Fact]
    public async Task Warehouses_ParentLocationUnreachable_Returns403_AndHappyPathFiltersByLocation()
    {
        var f = NewFixture();
        var other = AddCompany(f.Db, "Other", T0.AddDays(1));
        // Unreachable parent location (belongs to a company with no enabled connection).
        var otherLoc = AddLocation(f.Db, other.Id, "OtherLoc", T0.AddDays(2));
        await f.Db.SaveChangesAsync();

        var handler = new ListWarehousesHandler(f.Db, new AccessCheckService(f.Db, f.User));
        var denied = await handler.HandleAsync(new ListWarehousesQuery(otherLoc.Id, null, null), default);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ProblemHttpResult>(denied.Result).StatusCode);

        // Happy path: a reachable location with a warehouse under it.
        var homeLoc = AddLocation(f.Db, f.HomeCompanyId, "HomeLoc", T0.AddDays(3));
        var wh = new Warehouse { Id = Guid.NewGuid(), Name = "WH", LocationId = homeLoc.Id, CompanyId = f.HomeCompanyId, TenantId = Guid.NewGuid(), CreatedAtUtc = T0.AddDays(4) };
        f.Db.Add(wh);
        SetCursor(f.Db, wh, T0.AddDays(4));
        await f.Db.SaveChangesAsync();

        var ok = await handler.HandleAsync(new ListWarehousesQuery(homeLoc.Id, null, null), default);
        var page = Assert.IsType<Ok<MasterDataPage<WarehouseDto>>>(ok.Result).Value!;
        var row = Assert.Single(page.Items);
        Assert.Equal(wh.Id, row.Id);
        Assert.Equal(homeLoc.Id, row.LocationId);
    }

    [Fact]
    public async Task Racks_And_Bins_HappyPath_ResolveTheirParents()
    {
        var f = NewFixture();
        var loc = AddLocation(f.Db, f.HomeCompanyId, "L", T0.AddDays(1));
        var wh = new Warehouse { Id = Guid.NewGuid(), Name = "W", LocationId = loc.Id, CompanyId = f.HomeCompanyId, TenantId = Guid.NewGuid(), CreatedAtUtc = T0.AddDays(2) };
        f.Db.Add(wh); SetCursor(f.Db, wh, T0.AddDays(2));
        var rack = new Rack { Id = Guid.NewGuid(), Name = "R", WarehouseId = wh.Id, LocationId = loc.Id, CompanyId = f.HomeCompanyId, TenantId = Guid.NewGuid(), CreatedAtUtc = T0.AddDays(3) };
        f.Db.Add(rack); SetCursor(f.Db, rack, T0.AddDays(3));
        var bin = new Bin { Id = Guid.NewGuid(), Name = "B", RackId = rack.Id, WarehouseId = wh.Id, LocationId = loc.Id, CompanyId = f.HomeCompanyId, TenantId = Guid.NewGuid(), CreatedAtUtc = T0.AddDays(4) };
        f.Db.Add(bin); SetCursor(f.Db, bin, T0.AddDays(4));
        await f.Db.SaveChangesAsync();

        var racksHandler = new ListRacksHandler(f.Db, new AccessCheckService(f.Db, f.User));
        var rackPage = Assert.IsType<Ok<MasterDataPage<RackDto>>>((await racksHandler.HandleAsync(new ListRacksQuery(wh.Id, null, null), default)).Result).Value!;
        var rackRow = Assert.Single(rackPage.Items);
        Assert.Equal(rack.Id, rackRow.Id);
        Assert.Equal(wh.Id, rackRow.WarehouseId);

        var binsHandler = new ListBinsHandler(f.Db, new AccessCheckService(f.Db, f.User));
        var binPage = Assert.IsType<Ok<MasterDataPage<BinDto>>>((await binsHandler.HandleAsync(new ListBinsQuery(rack.Id, null, null), default)).Result).Value!;
        var binRow = Assert.Single(binPage.Items);
        Assert.Equal(bin.Id, binRow.Id);
        Assert.Equal(rack.Id, binRow.RackId);
    }

    // ── Malformed cursor → 400 validation_failed (handler-level, per endpoint) ─
    // QA gap: SyncCursor.TryDecode was unit-tested in isolation but its `false` return was discarded by
    // every handler, so a malformed cursor was silently served a start-of-world page. These assert each
    // handler actually maps that to 400 validation_failed.

    private const string MalformedCursor = "not-a-valid-cursor!!!";

    private static void AssertMalformedCursor(IResult actual)
    {
        var problem = Assert.IsType<ProblemHttpResult>(actual);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("validation_failed", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task Companies_MalformedCursor_Returns400ValidationFailed()
    {
        var f = NewFixture();
        var result = await CompaniesHandler(f).HandleAsync(new ListCompaniesQuery(MalformedCursor, null), default);
        AssertMalformedCursor(result.Result);
    }

    [Fact]
    public async Task Locations_MalformedCursor_Returns400ValidationFailed()
    {
        var f = NewFixture();
        var result = await LocationsHandler(f).HandleAsync(new ListLocationsQuery(null, MalformedCursor, null), default);
        AssertMalformedCursor(result.Result);
    }

    [Fact]
    public async Task Warehouses_MalformedCursor_Returns400ValidationFailed()
    {
        var f = NewFixture();
        var handler = new ListWarehousesHandler(f.Db, new AccessCheckService(f.Db, f.User));
        var result = await handler.HandleAsync(new ListWarehousesQuery(null, MalformedCursor, null), default);
        AssertMalformedCursor(result.Result);
    }

    [Fact]
    public async Task Racks_MalformedCursor_Returns400ValidationFailed()
    {
        var f = NewFixture();
        var handler = new ListRacksHandler(f.Db, new AccessCheckService(f.Db, f.User));
        var result = await handler.HandleAsync(new ListRacksQuery(null, MalformedCursor, null), default);
        AssertMalformedCursor(result.Result);
    }

    [Fact]
    public async Task Bins_MalformedCursor_Returns400ValidationFailed()
    {
        var f = NewFixture();
        var handler = new ListBinsHandler(f.Db, new AccessCheckService(f.Db, f.User));
        var result = await handler.HandleAsync(new ListBinsQuery(null, MalformedCursor, null), default);
        AssertMalformedCursor(result.Result);
    }

    // ── Cursor round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void SyncCursor_RoundTrips_AndTreatsNullEmptyAsStartOfWorld()
    {
        var ts = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        var encoded = SyncCursor.Encode(ts, id);

        Assert.True(SyncCursor.TryDecode(encoded, out var ts2, out var id2));
        Assert.Equal(ts, ts2);
        Assert.Equal(id, id2);

        Assert.True(SyncCursor.TryDecode(null, out var ts3, out var id3));
        Assert.Equal(DateTime.MinValue, ts3);
        Assert.Equal(Guid.Empty, id3);

        Assert.True(SyncCursor.TryDecode("", out _, out _));
        Assert.False(SyncCursor.TryDecode("!!!not-base64!!!", out _, out _));
    }
}
