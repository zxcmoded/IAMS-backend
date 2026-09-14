using Iams.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iams.Persistence;

// Conventions established here (see docs/schema/README.md):
//  * GUID PKs (EF generates sequential GUIDs client-side → clustering-friendly).
//  * Enums persisted as strings (HasConversion<string>) + CHECK constraint domain → resilient to reordering.
//  * All hierarchy/connection FKs use DeleteBehavior.Restrict to avoid SQL Server multiple-cascade-path
//    errors and to force explicit/soft deletes.
//  * CreatedAtUtc defaults to SYSUTCDATETIME() at the DB.

internal static class EnumCheck
{
    public static void AddEnumCheck<TEnum>(this EntityTypeBuilder b, string column) where TEnum : struct, Enum
    {
        var allowed = string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"));
        b.ToTable(t => t.HasCheckConstraint($"CK_{b.Metadata.GetTableName()}_{column}", $"[{column}] IN ({allowed})"));
    }
}

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.AddEnumCheck<TenantKind>(nameof(Tenant.Kind));
        b.HasIndex(x => x.Kind);
    }
}

public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("Companies");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.HasOne(x => x.Tenant).WithMany(t => t.Companies)
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TenantId);
    }
}

internal static class NodeConfig
{
    public static void ConfigureNodeBasics<T>(EntityTypeBuilder<T> b) where T : HierarchyNode
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.HasIndex(x => x.TenantId);
    }
}

public class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> b)
    {
        b.ToTable("Locations");
        NodeConfig.ConfigureNodeBasics(b);
        b.Property(x => x.Region).HasMaxLength(200);
        b.HasOne(x => x.Company).WithMany(c => c.Locations)
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CompanyId);                // list locations in a company
        b.HasIndex(x => new { x.CompanyId, x.Region }); // region filtering
    }
}

public class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("Warehouses");
        NodeConfig.ConfigureNodeBasics(b);
        b.HasOne(x => x.Location).WithMany(l => l.Warehouses)
            .HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.LocationId);
        b.HasIndex(x => x.CompanyId);  // denormalized-ancestry lookups
    }
}

public class RackConfiguration : IEntityTypeConfiguration<Rack>
{
    public void Configure(EntityTypeBuilder<Rack> b)
    {
        b.ToTable("Racks");
        NodeConfig.ConfigureNodeBasics(b);
        b.HasOne(x => x.Warehouse).WithMany(w => w.Racks)
            .HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.WarehouseId);
        b.HasIndex(x => x.CompanyId);
    }
}

public class BinConfiguration : IEntityTypeConfiguration<Bin>
{
    public void Configure(EntityTypeBuilder<Bin> b)
    {
        b.ToTable("Bins");
        NodeConfig.ConfigureNodeBasics(b);
        b.HasOne(x => x.Rack).WithMany(r => r.Bins)
            .HasForeignKey(x => x.RackId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RackId);
        b.HasIndex(x => x.WarehouseId);
        b.HasIndex(x => x.CompanyId);
    }
}

public class CompanyConnectionConfiguration : IEntityTypeConfiguration<CompanyConnection>
{
    public void Configure(EntityTypeBuilder<CompanyConnection> b)
    {
        b.ToTable("CompanyConnections", t =>
        {
            t.HasCheckConstraint("CK_CompanyConnections_NoSelf", "[SourceCompanyId] <> [TargetCompanyId]");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.ConnectionType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PermissionLevel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.EffectiveFromUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.PolicyRevision).IsConcurrencyToken(false);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.AddEnumCheck<ConnectionType>(nameof(CompanyConnection.ConnectionType));
        b.AddEnumCheck<PermissionLevel>(nameof(CompanyConnection.PermissionLevel));

        b.HasOne(x => x.SourceCompany).WithMany()
            .HasForeignKey(x => x.SourceCompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.TargetCompany).WithMany()
            .HasForeignKey(x => x.TargetCompanyId).OnDelete(DeleteBehavior.Restrict);

        // Hot path: one enabled connection per directed company pair; INCLUDE covers the read so the
        // access check is a single index seek with no key lookup.
        b.HasIndex(x => new { x.SourceCompanyId, x.TargetCompanyId })
            .IsUnique()
            .IncludeProperties(x => new { x.IsEnabled, x.PermissionLevel, x.ConnectionType, x.PolicyRevision });
    }
}

public class CompanyConnectionScopeConfiguration : IEntityTypeConfiguration<CompanyConnectionScope>
{
    public void Configure(EntityTypeBuilder<CompanyConnectionScope> b)
    {
        b.ToTable("CompanyConnectionScopes", t =>
        {
            // Exactly one scope FK populated, matching Level.
            t.HasCheckConstraint("CK_ConnScope_OneTarget",
                "(CASE WHEN [ScopeCompanyId]   IS NOT NULL THEN 1 ELSE 0 END" +
                " + CASE WHEN [ScopeLocationId]  IS NOT NULL THEN 1 ELSE 0 END" +
                " + CASE WHEN [ScopeWarehouseId] IS NOT NULL THEN 1 ELSE 0 END" +
                " + CASE WHEN [ScopeRackId]      IS NOT NULL THEN 1 ELSE 0 END" +
                " + CASE WHEN [ScopeBinId]       IS NOT NULL THEN 1 ELSE 0 END) = 1");
            t.HasCheckConstraint("CK_ConnScope_LevelMatches",
                "([Level] = 'Company'   AND [ScopeCompanyId]   IS NOT NULL) OR" +
                "([Level] = 'Location'  AND [ScopeLocationId]  IS NOT NULL) OR" +
                "([Level] = 'Warehouse' AND [ScopeWarehouseId] IS NOT NULL) OR" +
                "([Level] = 'Rack'      AND [ScopeRackId]      IS NOT NULL) OR" +
                "([Level] = 'Bin'       AND [ScopeBinId]       IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Level).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PermissionLevelOverride).HasConversion<string>().HasMaxLength(20);
        b.AddEnumCheck<HierarchyLevel>(nameof(CompanyConnectionScope.Level));

        b.HasOne(x => x.Connection).WithMany(c => c.Scopes)
            .HasForeignKey(x => x.CompanyConnectionId).OnDelete(DeleteBehavior.Cascade);

        // All scope-target FKs are NoAction (no cascade) — targets are hierarchy nodes protected by soft-delete.
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.ScopeCompanyId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Location>().WithMany().HasForeignKey(x => x.ScopeLocationId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.ScopeWarehouseId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Rack>().WithMany().HasForeignKey(x => x.ScopeRackId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Bin>().WithMany().HasForeignKey(x => x.ScopeBinId).OnDelete(DeleteBehavior.NoAction);

        // Hot path: load all scopes for a connection, covering the scope columns.
        b.HasIndex(x => x.CompanyConnectionId)
            .IncludeProperties(x => new
            {
                x.Level, x.ScopeCompanyId, x.ScopeLocationId, x.ScopeWarehouseId,
                x.ScopeRackId, x.ScopeBinId, x.PermissionLevelOverride
            });
    }
}

public class CompanyConnectionFilterConfiguration : IEntityTypeConfiguration<CompanyConnectionFilter>
{
    public void Configure(EntityTypeBuilder<CompanyConnectionFilter> b)
    {
        b.ToTable("CompanyConnectionFilters");
        b.HasKey(x => x.Id);
        b.Property(x => x.FilterType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FilterValue).HasMaxLength(400).IsRequired();
        b.AddEnumCheck<ConnectionFilterType>(nameof(CompanyConnectionFilter.FilterType));
        b.HasOne(x => x.Connection).WithMany(c => c.Filters)
            .HasForeignKey(x => x.CompanyConnectionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.CompanyConnectionId);
    }
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Username).HasMaxLength(256).IsRequired();
        b.Property(x => x.NormalizedUsername).HasMaxLength(256).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        b.Property(x => x.SecurityStamp).HasMaxLength(128).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.HasIndex(x => x.NormalizedUsername).IsUnique(); // login lookup
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.Description).HasMaxLength(400);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public class UserCompanyMembershipConfiguration : IEntityTypeConfiguration<UserCompanyMembership>
{
    public void Configure(EntityTypeBuilder<UserCompanyMembership> b)
    {
        b.ToTable("UserCompanyMemberships");
        b.HasKey(x => x.Id);
        b.HasOne(x => x.User).WithMany(u => u.Memberships)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Company).WithMany()
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Role).WithMany()
            .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.UserId, x.CompanyId }).IsUnique(); // one membership per user/company
        b.HasIndex(x => x.CompanyId);
    }
}

public class UserTwoFactorSettingConfiguration : IEntityTypeConfiguration<UserTwoFactorSetting>
{
    public void Configure(EntityTypeBuilder<UserTwoFactorSetting> b)
    {
        b.ToTable("UserTwoFactorSettings");
        b.HasKey(x => x.UserId); // 1:1 with User
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SharedSecret).HasMaxLength(256);
        b.AddEnumCheck<TwoFactorChannel>(nameof(UserTwoFactorSetting.Channel));
        b.HasOne(x => x.User).WithOne(u => u.TwoFactorSetting)
            .HasForeignKey<UserTwoFactorSetting>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class OtpChallengeConfiguration : IEntityTypeConfiguration<OtpChallenge>
{
    public void Configure(EntityTypeBuilder<OtpChallenge> b)
    {
        b.ToTable("OtpChallenges");
        b.HasKey(x => x.Id);
        b.Property(x => x.ChallengeToken).HasMaxLength(256).IsRequired();
        b.Property(x => x.CodeHash).HasMaxLength(256).IsRequired();
        b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.AddEnumCheck<OtpPurpose>(nameof(OtpChallenge.Purpose));
        b.AddEnumCheck<TwoFactorChannel>(nameof(OtpChallenge.Channel));
        b.HasOne(x => x.User).WithMany()
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        // Primary verify/resend lookup: client echoes the opaque token, no username needed.
        b.HasIndex(x => x.ChallengeToken).IsUnique();
        // Secondary: find/invalidate a user's outstanding challenges (rate-limiting, reissue).
        b.HasIndex(x => new { x.UserId, x.Purpose })
            .HasFilter("[ConsumedAtUtc] IS NULL");
    }
}

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("UserSessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeviceId).HasMaxLength(200);
        b.Property(x => x.RefreshTokenHash).HasMaxLength(256);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.HasOne(x => x.User).WithMany(u => u.Sessions)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ActiveCompany).WithMany()
            .HasForeignKey(x => x.ActiveCompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ActiveLocation).WithMany()
            .HasForeignKey(x => x.ActiveLocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.UserId).HasFilter("[RevokedAtUtc] IS NULL"); // active sessions per user
        b.HasIndex(x => x.RefreshTokenHash);
    }
}
