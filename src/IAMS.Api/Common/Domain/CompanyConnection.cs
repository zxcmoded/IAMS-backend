namespace IAMS.Api.Common.Domain;

/// <summary>
/// A configured, directed connection from one company (source) to another (target). Existence alone does
/// not grant access — <see cref="IsEnabled"/> must be true (BR-TC-001/005) and a matching
/// <see cref="CompanyConnectionScope"/> must cover the requested resource (BR-TC-003).
///
/// Exactly one row per directed (source,target) pair (unique index). Multiplicity of scopes/permissions is
/// expressed through child <see cref="Scopes"/>, not multiple connection rows — this deliberately closes
/// the "permission precedence across multiple connections" gap by disallowing multiplicity.
/// </summary>
public class CompanyConnection
{
    public Guid Id { get; set; }

    /// <summary>Company whose users are requesting access ("A").</summary>
    public Guid SourceCompanyId { get; set; }
    public Company SourceCompany { get; set; } = null!;

    /// <summary>Company being accessed ("B").</summary>
    public Guid TargetCompanyId { get; set; }
    public Company TargetCompany { get; set; } = null!;

    public ConnectionType ConnectionType { get; set; }

    /// <summary>Master on/off switch. A disabled connection grants nothing.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Default permission for this connection; a scope may override it.</summary>
    public PermissionLevel PermissionLevel { get; set; }

    // --- Offline-staleness / dynamic-policy support (BR-TC-007/008) ---

    /// <summary>
    /// Monotonically increasing, application-managed revision bumped on any policy-affecting change
    /// (enable/disable, permission change, scope add/remove/modify). Mobile stamps queued operations with
    /// the revision they relied on; at sync the backend compares it against the current value to detect
    /// staleness. The bump is enforced atomically inside <c>IamsDbContext.SaveChanges</c>, so it can never
    /// be forgotten by a mutation path.
    /// </summary>
    public long PolicyRevision { get; set; }

    /// <summary>When the current policy state took effect.</summary>
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<CompanyConnectionScope> Scopes { get; set; } = new List<CompanyConnectionScope>();
    public ICollection<CompanyConnectionFilter> Filters { get; set; } = new List<CompanyConnectionFilter>();
}

/// <summary>
/// One granted scope within a connection. <see cref="Level"/> selects which single scope FK is populated
/// (enforced by a check constraint). The resource's denormalized ancestry is compared directly against the
/// populated FK to decide whether the resource falls inside this scope.
/// </summary>
public class CompanyConnectionScope
{
    public Guid Id { get; set; }

    public Guid CompanyConnectionId { get; set; }
    public CompanyConnection Connection { get; set; } = null!;

    public HierarchyLevel Level { get; set; }

    // Exactly one of the following is non-null, matching Level (check constraint enforced).
    public Guid? ScopeCompanyId { get; set; }
    public Guid? ScopeLocationId { get; set; }
    public Guid? ScopeWarehouseId { get; set; }
    public Guid? ScopeRackId { get; set; }
    public Guid? ScopeBinId { get; set; }

    /// <summary>
    /// Optional per-scope permission override. Null = inherit the connection's <see cref="CompanyConnection.PermissionLevel"/>.
    /// Present so the unresolved "permission per level vs inherited" gap is not foreclosed.
    /// </summary>
    public PermissionLevel? PermissionLevelOverride { get; set; }

    /// <summary>The id of the node at <see cref="Level"/> this scope is bound to (whichever scope FK is populated).</summary>
    public Guid? BoundNodeId => Level switch
    {
        HierarchyLevel.Company => ScopeCompanyId,
        HierarchyLevel.Location => ScopeLocationId,
        HierarchyLevel.Warehouse => ScopeWarehouseId,
        HierarchyLevel.Rack => ScopeRackId,
        HierarchyLevel.Bin => ScopeBinId,
        _ => null
    };
}

/// <summary>Extensible additional filter attached to a connection (region/warehouse/category/location).</summary>
public class CompanyConnectionFilter
{
    public Guid Id { get; set; }

    public Guid CompanyConnectionId { get; set; }
    public CompanyConnection Connection { get; set; } = null!;

    public ConnectionFilterType FilterType { get; set; }
    public string FilterValue { get; set; } = string.Empty;
}
