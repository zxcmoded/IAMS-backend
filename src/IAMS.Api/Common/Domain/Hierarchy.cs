namespace IAMS.Api.Common.Domain;

/// <summary>
/// Base for every physical-hierarchy node below <see cref="Company"/>. Each level denormalizes its full
/// ancestor id chain so scope checks are a set of direct, indexable column comparisons rather than a
/// recursive ancestry walk on the hot path. Ancestry columns are maintained on create/move.
/// </summary>
public abstract class HierarchyNode
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Top-level organizational unit. A <see cref="User"/> belongs to exactly one Company, and all data access
/// is scoped to it (further narrowed to assigned Locations for the location-restricted roles).
/// </summary>
public class Company
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Last modification timestamp. Kept for symmetry with <see cref="HierarchyNode"/> so Company can
    /// participate in the same COALESCE(UpdatedAtUtc, CreatedAtUtc) sync-cursor scheme as the other levels.
    /// </summary>
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<Location> Locations { get; set; } = new List<Location>();
}

public class Location : HierarchyNode
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>Optional region tag.</summary>
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
