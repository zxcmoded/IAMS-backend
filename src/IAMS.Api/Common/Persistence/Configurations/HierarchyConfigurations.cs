using IAMS.Api.Common.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IAMS.Api.Common.Persistence.Configurations;

// Conventions (see docs/schema/README.md):
//  * GUID PKs (EF generates sequential GUIDs client-side; native `uuid` column type).
//  * Enums persisted as strings (HasConversion<string>) + CHECK constraint domain → resilient to reordering.
//  * Hierarchy/connection FKs use DeleteBehavior.Restrict — the same denormalized-ancestry FK fan-out that
//    caused SQL Server "multiple cascade paths" errors, kept Restrict under Postgres too for one consistent
//    deletion story (soft-delete via IsActive), not because Postgres has the same restriction.
//  * CreatedAtUtc defaults to now() at the DB; all "...AtUtc" DateTime properties map to `timestamptz`
//    (see IamsDbContext.ConfigureConventions).

internal static class EnumCheck
{
    public static void AddEnumCheck<TEnum>(this EntityTypeBuilder b, string column) where TEnum : struct, Enum
    {
        var allowed = string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"));
        b.ToTable(t => t.HasCheckConstraint($"CK_{b.Metadata.GetTableName()}_{column}", $"\"{column}\" IN ({allowed})"));
    }
}

internal static class NodeConfig
{
    public static void ConfigureNodeBasics<T>(EntityTypeBuilder<T> b) where T : HierarchyNode
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.HasIndex(x => x.TenantId);
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
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
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
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.HasOne(x => x.Tenant).WithMany(t => t.Companies)
            .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
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
        b.HasIndex(x => x.CompanyId);
        b.HasIndex(x => new { x.CompanyId, x.Region });
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
        b.HasIndex(x => x.CompanyId);
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
