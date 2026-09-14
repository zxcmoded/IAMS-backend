using IAMS.Api.Common.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IAMS.Api.Common.Persistence.Configurations;

public class CompanyConnectionConfiguration : IEntityTypeConfiguration<CompanyConnection>
{
    public void Configure(EntityTypeBuilder<CompanyConnection> b)
    {
        b.ToTable("CompanyConnections", t =>
            t.HasCheckConstraint("CK_CompanyConnections_NoSelf", "[SourceCompanyId] <> [TargetCompanyId]"));
        b.HasKey(x => x.Id);
        b.Property(x => x.ConnectionType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PermissionLevel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.EffectiveFromUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.AddEnumCheck<ConnectionType>(nameof(CompanyConnection.ConnectionType));
        b.AddEnumCheck<PermissionLevel>(nameof(CompanyConnection.PermissionLevel));

        b.HasOne(x => x.SourceCompany).WithMany()
            .HasForeignKey(x => x.SourceCompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.TargetCompany).WithMany()
            .HasForeignKey(x => x.TargetCompanyId).OnDelete(DeleteBehavior.Restrict);

        // Hot path: exactly one connection per directed company pair; INCLUDE covers the access-check read.
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
        b.Ignore(x => x.BoundNodeId);
        b.AddEnumCheck<HierarchyLevel>(nameof(CompanyConnectionScope.Level));

        b.HasOne(x => x.Connection).WithMany(c => c.Scopes)
            .HasForeignKey(x => x.CompanyConnectionId).OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Company>().WithMany().HasForeignKey(x => x.ScopeCompanyId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Location>().WithMany().HasForeignKey(x => x.ScopeLocationId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.ScopeWarehouseId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Rack>().WithMany().HasForeignKey(x => x.ScopeRackId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Bin>().WithMany().HasForeignKey(x => x.ScopeBinId).OnDelete(DeleteBehavior.NoAction);

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
        b.Property(x => x.IsSystemAdmin).HasDefaultValue(false);
        b.HasIndex(x => x.NormalizedUsername).IsUnique();
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
        b.HasIndex(x => new { x.UserId, x.CompanyId }).IsUnique();
        b.HasIndex(x => x.CompanyId);
    }
}

public class UserTwoFactorSettingConfiguration : IEntityTypeConfiguration<UserTwoFactorSetting>
{
    public void Configure(EntityTypeBuilder<UserTwoFactorSetting> b)
    {
        b.ToTable("UserTwoFactorSettings");
        b.HasKey(x => x.UserId);
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
        b.Property(x => x.ChallengeToken).HasMaxLength(128).IsRequired();
        b.Property(x => x.CodeHash).HasMaxLength(256).IsRequired();
        b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.Ignore(x => x.IsConsumed);
        b.AddEnumCheck<OtpPurpose>(nameof(OtpChallenge.Purpose));
        b.AddEnumCheck<TwoFactorChannel>(nameof(OtpChallenge.Channel));
        b.HasOne(x => x.User).WithMany()
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ChallengeToken).IsUnique();
        b.HasIndex(x => new { x.UserId, x.Purpose }).HasFilter("[ConsumedAtUtc] IS NULL");
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
        b.Property(x => x.SecurityStamp).HasMaxLength(128);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.HasOne(x => x.User).WithMany(u => u.Sessions)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ActiveCompany).WithMany()
            .HasForeignKey(x => x.ActiveCompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ActiveLocation).WithMany()
            .HasForeignKey(x => x.ActiveLocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.UserId).HasFilter("[RevokedAtUtc] IS NULL");
        b.HasIndex(x => x.RefreshTokenHash);
    }
}

public class UserDeviceBindingConfiguration : IEntityTypeConfiguration<UserDeviceBinding>
{
    public void Configure(EntityTypeBuilder<UserDeviceBinding> b)
    {
        b.ToTable("UserDeviceBindings");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeviceId).HasMaxLength(200).IsRequired();
        b.Property(x => x.DeviceType).HasMaxLength(100);
        b.Property(x => x.DeviceName).HasMaxLength(200);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RegisteredAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.AddEnumCheck<DeviceBindingStatus>(nameof(UserDeviceBinding.Status));

        // One binding per user, ever — a reset flips Status rather than allowing a second row.
        b.HasOne(x => x.User).WithMany()
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ResetByUser).WithMany()
            .HasForeignKey(x => x.ResetByUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.UserId).IsUnique();
    }
}
