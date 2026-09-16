namespace IAMS.Api.Common.Domain;

/// <summary>
/// Base for every physical-hierarchy node. Each level denormalizes its full ancestor id chain so the
/// effective-access scope check is a set of direct, indexable column comparisons rather than a recursive
/// ancestry walk on the hot path. Ancestry columns are maintained on create/move.
/// </summary>
public abstract class HierarchyNode
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Root tenant this node ultimately belongs to (denormalized on every level for isolation checks).</summary>
    public Guid TenantId { get; set; }
}

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TenantKind Kind { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<Company> Companies { get; set; } = new List<Company>();
}

public class Company
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Last modification timestamp. Added for symmetry with <see cref="HierarchyNode"/> so Company can
    /// participate in the same COALESCE(UpdatedAtUtc, CreatedAtUtc) sync-cursor scheme as the other levels.
    /// </summary>
    public DateTime? UpdatedAtUtc { get; set; }

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public ICollection<Location> Locations { get; set; } = new List<Location>();
}

public class Location : HierarchyNode
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>Optional region tag used by connection region-filtering.</summary>
    public string? Region { get; set; }

    public ICollection<Warehouse> Warehouses { get; set; } = new List<Warehouse>();
}

public class Warehouse : HierarchyNode
{
    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    // Denormalized ancestry.
    public Guid CompanyId { get; set; }

    public ICollection<Rack> Racks { get; set; } = new List<Rack>();
}

public class Rack : HierarchyNode
{
    public Guid WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;

    // Denormalized ancestry.
    public Guid LocationId { get; set; }
    public Guid CompanyId { get; set; }

    public ICollection<Bin> Bins { get; set; } = new List<Bin>();
}

public class Bin : HierarchyNode
{
    public Guid RackId { get; set; }
    public Rack Rack { get; set; } = null!;

    // Denormalized ancestry.
    public Guid WarehouseId { get; set; }
    public Guid LocationId { get; set; }
    public Guid CompanyId { get; set; }
}
