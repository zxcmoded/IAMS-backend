using IAMS.Api.Common.Domain;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Persistence;

public class IamsDbContext : DbContext
{
    public IamsDbContext(DbContextOptions<IamsDbContext> options) : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Rack> Racks => Set<Rack>();
    public DbSet<Bin> Bins => Set<Bin>();

    public DbSet<CompanyConnection> CompanyConnections => Set<CompanyConnection>();
    public DbSet<CompanyConnectionScope> CompanyConnectionScopes => Set<CompanyConnectionScope>();
    public DbSet<CompanyConnectionFilter> CompanyConnectionFilters => Set<CompanyConnectionFilter>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserCompanyMembership> UserCompanyMemberships => Set<UserCompanyMembership>();
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

        // Optimistic concurrency on connection config edits and Activation Key device-binding writes (see
        // ActivateHandler) rides Postgres's `xmin` system column, mapped as a shadow "row version"
        // property — there's no SQL Server `rowversion` equivalent, and `xmin` only exists on the Npgsql
        // provider, not the in-memory test provider, so it's applied conditionally here rather than in the
        // entity configurations themselves.
        if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            modelBuilder.Entity<CompanyConnection>()
                .Property<uint>("xmin")
                .HasColumnName("xmin")
                .ValueGeneratedOnAddOrUpdate()
                .IsRowVersion();

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
            // (a) xmin optimistic-concurrency tokens for the mutable/config rows (same pattern as
            //     User/CompanyConnection above). Ledger/scan rows are append-only, so they need none.
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
        BumpPolicyRevisions();
        BumpStockLevelVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpPolicyRevisions();
        BumpStockLevelVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Integrity invariant for offline conflict detection (phase-2a §2.2 / §5): the application-managed
    /// <see cref="StockLevel.Version"/> is the monotonic semantic version the mobile stamps a queued mutation
    /// with and the backend compares against at push time. It MUST advance in the same transaction as every
    /// applied on-hand change, or a stale offline mutation could apply silently. Centralizing it here — exactly
    /// like <see cref="BumpPolicyRevisions"/> centralizes the connection revision — makes it impossible for any
    /// mutation path (receive / transfer / adjustment / count reconciliation) to forget to bump it.
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

    /// <summary>
    /// Integrity invariant for BR-TC-007/008: any policy-affecting change to a connection — its
    /// enabled state, permission level, or the set/contents of its scopes and filters — increments the
    /// connection's <see cref="CompanyConnection.PolicyRevision"/> in the SAME transaction as the change.
    /// Doing it here (rather than in each mutation handler) makes it impossible for a mutation path to
    /// forget, so mobile's offline-staleness detection can never silently break. Brand-new connections
    /// (State == Added) are not bumped — their revision is set at creation.
    /// </summary>
    private void BumpPolicyRevisions()
    {
        // Snapshot the change set once (each ChangeTracker.Entries<T>() call runs DetectChanges; do it up
        // front, then mutate, so we never enumerate the tracker while modifying it).
        ChangeTracker.DetectChanges();

        var connectionEntries = ChangeTracker.Entries<CompanyConnection>().ToList();
        var scopeEntries = ChangeTracker.Entries<CompanyConnectionScope>().ToList();
        var filterEntries = ChangeTracker.Entries<CompanyConnectionFilter>().ToList();

        var toBump = new HashSet<Guid>();

        foreach (var entry in connectionEntries)
        {
            if (entry.State == EntityState.Modified &&
                (entry.Property(nameof(CompanyConnection.IsEnabled)).IsModified ||
                 entry.Property(nameof(CompanyConnection.PermissionLevel)).IsModified))
            {
                toBump.Add(entry.Entity.Id);
            }
        }

        CollectParentIds(toBump, scopeEntries, s => s.CompanyConnectionId, connectionEntries);
        CollectParentIds(toBump, filterEntries, f => f.CompanyConnectionId, connectionEntries);

        if (toBump.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var entry in connectionEntries)
        {
            if (!toBump.Contains(entry.Entity.Id))
            {
                continue;
            }

            entry.Entity.PolicyRevision += 1;
            entry.Entity.UpdatedAtUtc = now;
            entry.Entity.EffectiveFromUtc = now;
            // Ensure the indirect (child-driven) bump is recorded even if the connection was Unchanged.
            if (entry.State == EntityState.Unchanged)
            {
                entry.State = EntityState.Modified;
            }
        }
    }

    private static void CollectParentIds<TChild>(
        HashSet<Guid> toBump,
        List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TChild>> childEntries,
        Func<TChild, Guid> parentId,
        List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<CompanyConnection>> connectionEntries)
        where TChild : class
    {
        foreach (var entry in childEntries)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var connectionId = parentId(entry.Entity);
            var exists = connectionEntries.Any(e => e.Entity.Id == connectionId && e.State != EntityState.Added);
            if (exists)
            {
                toBump.Add(connectionId);
            }
        }
    }
}
