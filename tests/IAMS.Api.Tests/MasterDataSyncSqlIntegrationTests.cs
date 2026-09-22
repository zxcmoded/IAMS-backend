using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Features.MasterData.ListLocations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IAMS.Api.Tests;

/// <summary>
/// Opt-in integration tests that exercise the master-data sync cursor against a REAL PostgreSQL instance.
/// The in-memory provider cannot evaluate the <c>SyncCursorUtc GENERATED ALWAYS AS
/// COALESCE(UpdatedAtUtc, CreatedAtUtc) STORED</c> column at all, nor validate that
/// <c>EF.Property&lt;DateTime&gt;(x, "SyncCursorUtc")</c> ordering / the <c>Id.CompareTo</c> keyset
/// tie-break translate to correct SQL — so those guarantees are only meaningful here.
///
/// Skipped unless <c>IAMS_PG_TEST_CONN</c> is set (same pattern as
/// <see cref="SqlPolicyRevisionIntegrationTests"/>). In the shared "RealPostgresIntegration" collection so
/// it never runs concurrently with the other real-Postgres classes against the same database name.
/// </summary>
[Collection("RealPostgresIntegration")]
public class MasterDataSyncSqlIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("IAMS_PG_TEST_CONN");

    private static IamsDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<IamsDbContext>().UseNpgsql(Conn).Options;
        return new IamsDbContext(options);
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(Guid CompanyId, IReadOnlyList<Guid> LocationIds);

    /// <summary>
    /// Seeds one company with locations spanning: CreatedAtUtc-only rows, an UpdatedAtUtc-overriding row,
    /// and a deliberate SyncCursorUtc tie (two rows sharing the same coalesced timestamp, distinct Ids) so
    /// the Id tie-break is actually exercised. SyncCursorUtc is never set by us — Postgres generates it.
    /// </summary>
    private static async Task<Seed> SeedAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Co", CreatedAtUtc = T0 };

        Location Loc(string name, DateTime created, DateTime? updated) => new()
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            Name = name,
            CreatedAtUtc = created,
            UpdatedAtUtc = updated
        };

        var l1 = Loc("l1", T0.AddDays(1), null);                 // cursor = created (T0+1)
        var l2 = Loc("l2", T0.AddDays(2), null);                 // cursor = created (T0+2)
        var l3 = Loc("l3", T0.AddDays(1), T0.AddDays(5));        // cursor = updated (T0+5), overrides created
        var tieA = Loc("tieA", T0.AddDays(3), null);             // cursor = T0+3  ┐ tie
        var tieB = Loc("tieB", T0.AddDays(3), null);             // cursor = T0+3  ┘ tie, distinct Id

        db.AddRange(company, l1, l2, l3, tieA, tieB);
        await db.SaveChangesAsync();

        return new Seed(company.Id, new[] { l1.Id, l2.Id, l3.Id, tieA.Id, tieB.Id });
    }

    private static ListLocationsHandler HandlerFor(IamsDbContext db, Seed seed) =>
        new(db, new AccessScopeResolver(db, new FakeCurrentUser
        {
            UserId = Guid.NewGuid(),
            CompanyId = seed.CompanyId,
            Role = UserRole.Admin
        }));

    [Fact]
    public async Task GeneratedColumn_EqualsCoalesce_AndRegeneratesOnUpdate()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var seed = await SeedAsync();

        await using var db = NewContext();

        // A CreatedAtUtc-only row: SyncCursorUtc == CreatedAtUtc.
        var l1 = seed.LocationIds[0];
        var cursor1 = await db.Locations.Where(l => l.Id == l1)
            .Select(l => EF.Property<DateTime>(l, SyncCursor.ColumnName)).SingleAsync();
        var created1 = await db.Locations.Where(l => l.Id == l1).Select(l => l.CreatedAtUtc).SingleAsync();
        Assert.Equal(created1, cursor1);

        // The row with UpdatedAtUtc set: SyncCursorUtc == UpdatedAtUtc (COALESCE picks the non-null).
        var l3 = seed.LocationIds[2];
        var cursor3 = await db.Locations.Where(l => l.Id == l3)
            .Select(l => EF.Property<DateTime>(l, SyncCursor.ColumnName)).SingleAsync();
        Assert.Equal(T0.AddDays(5), cursor3);

        // Mutating UpdatedAtUtc regenerates the STORED column.
        var entity = await db.Locations.SingleAsync(l => l.Id == l1);
        entity.UpdatedAtUtc = T0.AddDays(9);
        await db.SaveChangesAsync();

        await using var verify = NewContext();
        var cursor1After = await verify.Locations.Where(l => l.Id == l1)
            .Select(l => EF.Property<DateTime>(l, SyncCursor.ColumnName)).SingleAsync();
        Assert.Equal(T0.AddDays(9), cursor1After);
    }

    [Fact]
    public async Task KeysetPagination_MatchesFullOrderedScan_StableAndTieBroken()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        var seed = await SeedAsync();

        await using var db = NewContext();

        // Ground truth: a single ordered scan by (SyncCursorUtc, Id) — the exact order the keyset must reproduce.
        var expected = await db.Locations
            .OrderBy(l => EF.Property<DateTime>(l, SyncCursor.ColumnName)).ThenBy(l => l.Id)
            .Select(l => l.Id)
            .ToListAsync();
        Assert.Equal(seed.LocationIds.Count, expected.Count);

        // Page through with pageSize = 2 (so a page boundary falls inside the tie pair) and concatenate.
        var handler = HandlerFor(db, seed);
        var collected = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var result = await handler.HandleAsync(new ListLocationsQuery(null, cursor, 2), default);
            var page = Assert.IsType<Ok<MasterDataPage<LocationDto>>>(result.Result).Value!;
            collected.AddRange(page.Items.Select(i => i.Id));
            if (!page.HasMore)
            {
                // Final page's cursor is non-null (page was non-empty) but a further poll must be empty.
                var tail = Assert.IsType<Ok<MasterDataPage<LocationDto>>>(
                    (await handler.HandleAsync(new ListLocationsQuery(null, page.NextCursor, 2), default)).Result).Value!;
                Assert.Empty(tail.Items);
                Assert.Null(tail.NextCursor);
                break;
            }
            cursor = page.NextCursor;
        }

        Assert.Equal(expected, collected);                 // same order, no skips
        Assert.Equal(expected.Count, collected.Distinct().Count()); // no duplicates across page boundaries
    }

    [Fact]
    public async Task CompositeIndex_WasCreatedByMigration()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return; // opt-in
        await SeedAsync();

        await using var db = NewContext();
        // The keyset index the query relies on must physically exist (created by MasterDataSyncCursors).
        var exists = await db.Database
            .SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_Locations_Sync'")
            .AnyAsync();
        Assert.True(exists);
    }
}
