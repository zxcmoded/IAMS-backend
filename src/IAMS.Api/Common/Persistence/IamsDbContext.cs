using IAMS.Api.Common.Domain;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Persistence;

public class IamsDbContext : DbContext
{
    public IamsDbContext(DbContextOptions<IamsDbContext> options) : base(options)
    {
    }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Rack> Racks => Set<Rack>();
    public DbSet<Bin> Bins => Set<Bin>();

    public DbSet<User> Users => Set<User>();
    public DbSet<UserLocationAssignment> UserLocationAssignments => Set<UserLocationAssignment>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    // Phase 2a — F3 Scanning + F4 Inventory Operations.
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<InventorySettings> InventorySettings => Set<InventorySettings>();
    public DbSet<ScanEvent> ScanEvents => Set<ScanEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IamsDbContext).Assembly);

        // Optimistic concurrency on Activation Key device-binding writes (see ActivateHandler) rides
        // Postgres's `xmin` system column, mapped as a shadow "row version" property — there's no SQL Server
        // `rowversion` equivalent, and `xmin` only exists on the Npgsql provider, not the in-memory test
        // provider, so it's applied conditionally here rather than in the entity configurations themselves.
        if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            modelBuilder.Entity<User>()
                .Property<uint>("xmin")
                .HasColumnName("xmin")
                .ValueGeneratedOnAddOrUpdate()
                .IsRowVersion();

            // The master-data sync cursor is a STORED generated column, COALESCE(UpdatedAtUtc, CreatedAtUtc),
            // so every row has a single monotone-ish keyset value the composite indexes can order on. This is
            // applied only under Npgsql: the in-memory provider can't evaluate a Postgres generated column, so
            // there the shadow property (declared in HierarchyConfigurations) stays plain-writable for tests.
            const string cursorSql = "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")";
            modelBuilder.Entity<Company>()
                .Property<DateTime>(MasterData.SyncCursor.ColumnName)
                .HasComputedColumnSql(cursorSql, stored: true);
            modelBuilder.Entity<Location>()
                .Property<DateTime>(MasterData.SyncCursor.ColumnName)
                .HasComputedColumnSql(cursorSql, stored: true);
            modelBuilder.Entity<Warehouse>()
                .Property<DateTime>(MasterData.SyncCursor.ColumnName)
                .HasComputedColumnSql(cursorSql, stored: true);
            modelBuilder.Entity<Rack>()
                .Property<DateTime>(MasterData.SyncCursor.ColumnName)
                .HasComputedColumnSql(cursorSql, stored: true);
            modelBuilder.Entity<Bin>()
                .Property<DateTime>(MasterData.SyncCursor.ColumnName)
                .HasComputedColumnSql(cursorSql, stored: true);

            // ── Phase 2a (F3/F4) Npgsql-only wiring ─────────────────────────────────────────────────
            // (a) xmin optimistic-concurrency tokens for the mutable/config rows (same pattern as User
            //     above). Ledger/scan rows are append-only, so they need none.
            foreach (var clr in new[]
                     {
                         typeof(StockLevel), typeof(StockCount),
                         typeof(InventoryItem), typeof(InventorySettings)
                     })
            {
                modelBuilder.Entity(clr).Property<uint>("xmin")
                    .HasColumnName("xmin").ValueGeneratedOnAddOrUpdate().IsRowVersion();
            }

            // (b) The master-data sync cursor (COALESCE(UpdatedAtUtc, CreatedAtUtc) STORED) extended to the
            //     inventory tables that participate in offline sync (phase-2a §2.8). ScanEvents and
            //     InventorySettings are intentionally NOT cursor-synced.
            foreach (var clr in new[]
                     {
                         typeof(InventoryItem), typeof(StockLevel),
                         typeof(InventoryTransaction), typeof(StockCount)
                     })
            {
                modelBuilder.Entity(clr).Property<DateTime>(MasterData.SyncCursor.ColumnName)
                    .HasComputedColumnSql(cursorSql, stored: true);
            }

            // (c) StockCount.Variance is the STORED generated CountedQuantity - SystemQuantity — one
            //     authoritative definition so an inconsistent variance can never be written.
            modelBuilder.Entity<StockCount>().Property(x => x.Variance)
                .HasComputedColumnSql("\"CountedQuantity\" - \"SystemQuantity\"", stored: true);
        }

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Project-wide "UTC only" timestamp convention (see docs/schema/README.md): every DateTime
        // property maps to `timestamptz`, not the ambiguous `timestamp without time zone`.
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamptz");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpStockLevelVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpStockLevelVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Integrity invariant for offline conflict detection (phase-2a §2.2 / §5): the application-managed
    /// <see cref="StockLevel.Version"/> is the monotonic semantic version the mobile stamps a queued mutation
    /// with and the backend compares against at push time. It MUST advance in the same transaction as every
    /// applied on-hand change, or a stale offline mutation could apply silently. Centralizing it here makes it
    /// impossible for any mutation path (receive / transfer / adjustment / count reconciliation) to forget to
    /// bump it.
    ///
    /// A brand-new <see cref="StockLevel"/> (State == Added, e.g. a first receive into an empty bin) starts at
    /// <c>Version = 1</c>; an existing row whose <see cref="StockLevel.QuantityOnHand"/> changed increments and
    /// stamps <see cref="StockLevel.UpdatedAtUtc"/> so its sync cursor advances too.
    /// </summary>
    private void BumpStockLevelVersions()
    {
        ChangeTracker.DetectChanges();

        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<StockLevel>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.Version < 1)
                {
                    entry.Entity.Version = 1;
                }
            }
            else if (entry.State == EntityState.Modified &&
                     entry.Property(nameof(StockLevel.QuantityOnHand)).IsModified)
            {
                entry.Entity.Version += 1;
                entry.Entity.UpdatedAtUtc = now;
            }
        }
    }
}
