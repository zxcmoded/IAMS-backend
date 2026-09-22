using IAMS.Api.Common.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IAMS.Api.Common.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users", t =>
            // Role is a fixed, closed int-coded enum (not a lookup table); the CHECK keeps the column domain
            // exactly the five valid codes, mirroring the string-enum CHECK convention used elsewhere.
            t.HasCheckConstraint("CK_Users_Role", "\"Role\" IN (100, 200, 300, 700, 800)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Username).HasMaxLength(256).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Role).HasConversion<int>();
        b.Property(x => x.ActivationKeyHash).HasMaxLength(128).IsRequired();
        b.Property(x => x.ActivationStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ActivatedDeviceId).HasMaxLength(200);
        b.Property(x => x.SecurityStamp).HasMaxLength(128).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.AddEnumCheck<ActivationStatus>(nameof(User.ActivationStatus));

        // The Activation Key is the ONLY credential — this is the lookup index ActivateHandler queries by
        // (a hash of the presented key), so it must be unique: two users can never share a key.
        b.HasIndex(x => x.ActivationKeyHash).IsUnique();

        // Every user belongs to exactly one Company.
        b.HasOne(x => x.Company).WithMany()
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CompanyId);

        b.HasOne(x => x.ActivationResetByUser).WithMany()
            .HasForeignKey(x => x.ActivationResetByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class UserLocationAssignmentConfiguration : IEntityTypeConfiguration<UserLocationAssignment>
{
    public void Configure(EntityTypeBuilder<UserLocationAssignment> b)
    {
        b.ToTable("UserLocationAssignments");
        b.HasKey(x => x.Id);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");

        b.HasOne(x => x.User).WithMany(u => u.AssignedLocations)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Location).WithMany()
            .HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);

        // A user is assigned to a given Location at most once; the reverse index serves "who is assigned here".
        b.HasIndex(x => new { x.UserId, x.LocationId }).IsUnique();
        b.HasIndex(x => x.LocationId);
    }
}

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("UserSessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeviceId).HasMaxLength(200);
        b.Property(x => x.SecurityStamp).HasMaxLength(128);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.HasOne(x => x.User).WithMany(u => u.Sessions)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId).HasFilter("\"RevokedAtUtc\" IS NULL");
    }
}
