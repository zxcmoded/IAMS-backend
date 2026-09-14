using Iams.Domain;
using Microsoft.EntityFrameworkCore;

namespace Iams.Persistence;

public class IamsDbContext : DbContext
{
    public IamsDbContext(DbContextOptions<IamsDbContext> options) : base(options) { }

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IamsDbContext).Assembly);
    }
}
