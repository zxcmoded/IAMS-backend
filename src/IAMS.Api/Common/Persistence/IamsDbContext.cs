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
    public DbSet<UserTwoFactorSetting> UserTwoFactorSettings => Set<UserTwoFactorSetting>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserDeviceBinding> UserDeviceBindings => Set<UserDeviceBinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IamsDbContext).Assembly);

        // The SQL Server rowversion concurrency token isn't supported by the in-memory test provider;
        // relax it there so tests exercise real behavior without a provider-specific false concurrency
        // failure. Production (SQL Server) keeps optimistic concurrency on connection config edits and on
        // device-binding writes (see UserDeviceBinding.RowVersion / VerifyTwoFactorHandler).
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            modelBuilder.Entity<CompanyConnection>()
                .Property(x => x.RowVersion)
                .IsConcurrencyToken(false)
                .ValueGeneratedNever();

            modelBuilder.Entity<UserDeviceBinding>()
                .Property(x => x.RowVersion)
                .IsConcurrencyToken(false)
                .ValueGeneratedNever();
        }

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpPolicyRevisions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpPolicyRevisions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
