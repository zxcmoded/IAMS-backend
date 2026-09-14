# IAMS Backend — Schema Design (Phase 1)

**Scope:** F1 (Authentication & 2FA) and F15 (Parent/Child Tenant Connections).
**Status:** Design + verified EF Core model. The reference model in `reference-model/` compiles against
EF Core 10 / SQL Server and the initial migration generates cleanly (15 tables, fully reversible, no
multiple-cascade-path errors). See [Verification](#verification).

> **2FA verify/resend flow:** `OtpChallenges.ChallengeToken` is an opaque, globally-unique token
> returned to the client on login and echoed back on verify/resend — the client never re-sends
> username, and the raw OTP is only sent on verify (compared against `CodeHash`). It has a **unique
> index** (`IX_OtpChallenges_ChallengeToken`) and is the primary lookup for that flow; the
> `(UserId, Purpose)` filtered index remains for reissue/rate-limiting.
>
> **Active company is per-session:** resolved from `UserSessions.ActiveCompanyId` (+ optional
> `ActiveLocationId`), **not** a column on `User`. A user can be a member of multiple companies
> (`UserCompanyMemberships`); which one is active is session state, so `GET /me/scope` reads it off
> the session record. Cross-tenant connection scope is resolved dynamically per request (not frozen
> in the session) so policy changes are honored without re-issuing the session.

This document establishes the SQL Server / EF Core conventions the `dotnet-backend-engineer` should
build the persistence layer on. `initial-schema.sql` is the generated DDL for review.

---

## 1. What this covers

| Area | Tables |
|---|---|
| Physical hierarchy | `Tenants`, `Companies`, `Locations`, `Warehouses`, `Racks`, `Bins` |
| Cross-tenant connections | `CompanyConnections`, `CompanyConnectionScopes`, `CompanyConnectionFilters` |
| Identity / auth / 2FA | `Users`, `Roles`, `UserCompanyMemberships`, `UserTwoFactorSettings`, `OtpChallenges`, `UserSessions` |

---

## 2. Key design decisions (and why)

### 2.1 Explicit per-level tables, not a self-referencing node table
The hierarchy is **fixed and shallow** (Tenant → Company → Location → Warehouse → Rack → Bin — always
exactly these six levels). Self-referencing tables earn their keep for *arbitrary-depth* trees; here the
depth is known and each level carries distinct attributes (a `Location` has a `Region`; a `Bin` does
not). Explicit tables give us **real FK integrity** (a `Warehouse` cannot exist without a valid
`Location`), type-safe EF navigation, and per-level index tuning. Trade-off addressed below by
denormalizing ancestry.

### 2.2 Denormalized ancestry on every level — the hot-path enabler
Each hierarchy row stores its **full ancestor id chain**, not just its direct parent:

- `Warehouse` → `LocationId`, `CompanyId`, `TenantId`
- `Rack` → `WarehouseId`, `LocationId`, `CompanyId`, `TenantId`
- `Bin` → `RackId`, `WarehouseId`, `LocationId`, `CompanyId`, `TenantId`

This turns the effective-access scope check ("is this bin inside a scope defined at warehouse W?") into a
**single indexed column comparison** instead of a recursive ancestry walk on every cross-tenant request.
The physical topology is stable (bins don't hop between racks), so these columns are effectively
write-once; if a node *is* relocated, the ancestry columns are updated as part of that controlled
operation and access is always evaluated against current ancestry (this is the correct behavior for the
spec's "resource moves to a restricted warehouse" edge case).

### 2.3 Connection model: one directed connection + child scopes + child filters
- `CompanyConnections` — one row per directed `(SourceCompanyId, TargetCompanyId)` pair. `SourceCompany`
  = the company whose users request access ("A"); `TargetCompany` = the company being accessed ("B").
  Direction is stored explicitly (`ConnectionType`) and evaluated independently — **P→C never implies
  C→P** (BR-TC-002). A `CHECK (SourceCompanyId <> TargetCompanyId)` blocks self-connections.
- `CompanyConnectionScopes` — a connection grants access to one or more **specific** nodes. Each scope
  row names a `Level` and exactly one matching scope FK (`ScopeCompanyId` / `ScopeLocationId` / … /
  `ScopeBinId`), enforced by two CHECK constraints. This is why a company-level connection does **not**
  auto-grant every child (BR-TC-003) — access is exactly the scopes you list.
- `CompanyConnectionFilters` — extensible region/warehouse/category/location filters (BR spec
  "Filtering").
- **`IsEnabled = 0` grants nothing** (BR-TC-001/005): existence of a connection is never sufficient.

### 2.4 Offline staleness / dynamic policy (BR-TC-007, BR-TC-008)
`CompanyConnections` carries:
- **`PolicyRevision` (bigint)** — application-incremented on any policy-affecting change (enable/disable,
  scope add/remove, permission change). The mobile client **stamps each queued operation** with the
  `PolicyRevision` (and connection id) it relied on. At sync, the backend compares the stamped revision
  against the current one; if it changed, the operation is re-evaluated against current policy and
  rejected if no longer permitted. A queued op therefore **cannot bypass a restriction discovered at
  sync time**.
- **`EffectiveFromUtc`** — when the current policy took effect.
- **`RowVersion` (rowversion)** — optimistic concurrency for admin config edits (distinct concern from
  `PolicyRevision`, which is the semantic, client-facing version).

### 2.5 Permission level: connection default + optional per-scope override
`PermissionLevel` (Read/Write/Full) is required on the connection. `CompanyConnectionScopes` has a
**nullable** `PermissionLevelOverride`. This deliberately keeps the unresolved gaps open: whether
permissions inherit down the hierarchy or are set per level, and whether a role further restricts them,
can both be resolved later without a schema change.

### 2.6 GUID primary keys
All PKs are `uniqueidentifier`. EF Core generates **sequential** GUIDs client-side, which keeps clustered
indexes from fragmenting. Rationale: multi-tenant isolation, safe id generation across environments, and
forward-compatibility with offline/mobile-generated ids in later phases (F11 sync).

### 2.7 Enums as strings + CHECK constraints
Enums persist as `nvarchar(20)` via `HasConversion<string>()`, each backed by a `CHECK … IN (…)`
constraint. Strings are self-documenting in the DB and immune to the "someone reordered the enum" class
of bug that int-backed enums invite. The CHECK makes an out-of-domain value **unrepresentable**.

### 2.8 `DeleteBehavior.Restrict` / `NoAction` across hierarchy & connections
Because ancestry is denormalized, several FKs point at the same ancestor table, which would produce SQL
Server "multiple cascade paths" errors under cascade. All hierarchy and connection FKs use
`Restrict`/`NoAction`; deletion is handled via **soft-delete (`IsActive`)** and explicit application
logic. Only genuinely user-owned children cascade (`OtpChallenges`, `UserSessions`,
`UserCompanyMemberships`, `UserTwoFactorSettings`, `CompanyConnectionScopes`/`Filters` off their
connection). Verified: the initial migration generates with **zero** cascade-path errors.

---

## 3. Effective-access evaluation — the hot path

The backend service layer (not built here) runs this on **every cross-tenant resource access**. The
schema is shaped so each step is an index seek:

```
Input: userId, targetResource (level + its denormalized ancestry ids), requested op

1. Resolve the user's source company/companies:
     SELECT CompanyId FROM UserCompanyMemberships WHERE UserId = @userId
     -- covered by UNIQUE IX_UserCompanyMemberships_UserId_CompanyId
     (active company usually comes straight off UserSessions.ActiveCompanyId)

2. Same tenant?  resource.TenantId == sourceCompany.TenantId
     → YES: normal tenant authorization, done.
     → NO:  continue.

3. Find the connection (single seek, no key lookup — covering index):
     SELECT IsEnabled, PermissionLevel, ConnectionType, PolicyRevision, Id
     FROM CompanyConnections
     WHERE SourceCompanyId = @sourceCompanyId AND TargetCompanyId = @targetCompanyId
     -- served entirely by UNIQUE IX_CompanyConnections_SourceCompanyId_TargetCompanyId
     --   INCLUDE (IsEnabled, PermissionLevel, ConnectionType, PolicyRevision)
     → no row      → Access Denied (BR-TC-001)
     → IsEnabled=0 → Access Denied (BR-TC-005)

4. Does any scope cover the resource? (single seek, covering index):
     SELECT Level, ScopeCompanyId, …, ScopeBinId, PermissionLevelOverride
     FROM CompanyConnectionScopes
     WHERE CompanyConnectionId = @connId
     -- served by IX_CompanyConnectionScopes_CompanyConnectionId INCLUDE(scope cols)
   Match in memory against the resource's denormalized ancestry:
     Company-level scope   → ScopeCompanyId   == resource.CompanyId
     Location-level scope  → ScopeLocationId  == resource.LocationId
     Warehouse-level scope → ScopeWarehouseId == resource.WarehouseId
     Rack-level scope      → ScopeRackId      == resource.RackId
     Bin-level scope       → ScopeBinId       == resource.BinId (or resource.Id when it's a bin)
     → no match → Access Denied (BR-TC-003)

5. Permission = matched scope's PermissionLevelOverride ?? connection.PermissionLevel
     Read → view only · Write → permitted writes · Full → full permitted ops (BR-TC-004)
```

Steps 3 and 4 are each one covering-index seek keyed by ids already in hand — no ancestry recursion, no
table lookups. That is the entire point of the denormalized-ancestry + covering-index design.

---

## 4. Conventions for `dotnet-backend-engineer`

- **Target:** .NET 10 / EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer` `10.0.x`). This is what the
  reference model is verified against; change deliberately, not by accident.
- **Entities** live in the domain layer; **configuration** in `IEntityTypeConfiguration<T>` classes
  registered via `modelBuilder.ApplyConfigurationsFromAssembly(...)` — never inline in `OnModelCreating`.
- **Enums:** `HasConversion<string>()` + `HasMaxLength(20)` + a `CHECK … IN (…)` (see the `AddEnumCheck`
  helper in `Configurations.cs`).
- **Timestamps:** UTC only (`…AtUtc`), `CreatedAtUtc` defaults to `SYSUTCDATETIME()` at the DB.
- **Deletes:** `Restrict`/`NoAction` on cross-aggregate FKs; soft-delete via `IsActive`.
- **Migrations:** generate with `dotnet ef migrations add <Name>` into `Persistence/Migrations`. Name them
  in PascalCase describing the change (`InitialCreate`, `AddAssetRegister`, …). Do **not** hand-edit the
  designer/snapshot files; do review the generated `Up`/`Down` SQL before committing.
- The migration history table is `__EFMigrationsHistory` (default).

### Applying it in-repo
The reference model was verified in a throwaway project. Once the real solution/project exists, drop
`reference-model/Domain` and `reference-model/Persistence` into the chosen projects, wire a real
connection string / `IDesignTimeDbContextFactory`, and run `dotnet ef migrations add InitialCreate`. The
generated migration will match `initial-schema.sql`.

---

## 5. Migration safety & rollback

- **Initial migration on a greenfield DB:** no existing rows, so **no backfill and no
  default-value-for-new-non-null-column concerns**. Pure `CREATE`.
- **Reversible:** the `Down` drops all 15 tables (verified). On a populated DB a rollback is destructive
  by nature (it's the initial create) — that is expected only pre-launch.
- **Concurrency:** greenfield create runs before any traffic, so no live-table locking concern for *this*
  migration. Future migrations that touch the hot tables (`CompanyConnections`, hierarchy) must consider
  online-index and lock implications — flagged for later phases.
- **`PolicyRevision` is application-managed**, not a DB trigger. The backend **must** increment it inside
  the same transaction as any policy-affecting change, or offline staleness detection (BR-TC-008) breaks.
  This is the single most important integrity obligation handed to the service layer.

---

## 6. Open gaps deliberately NOT foreclosed

| Gap (from spec) | How the schema stays open |
|---|---|
| Lockout threshold / offline-login policy (F1) | `Users` has no lockout mechanics invented; columns can be added later without touching existing data. |
| Permission inheritance vs per-level (F15) | `PermissionLevelOverride` nullable on scope; either resolution works. |
| Role can further restrict connection permission (F15) | `Roles` + `UserCompanyMemberships.RoleId` scaffolded but not enforced. |
| Multiple simultaneous connections per target, precedence unclear (F15) | Phase-1 enforces one connection per directed pair via a unique index (deterministic default). **If** the gap resolves toward multiple connections, drop that unique index and add a precedence/priority column — a small, safe migration. See RISKS in the handoff report. |

---

## 7. Verification

Performed against EF Core 10 + SQL Server provider:
- `dotnet build` — succeeds, 0 warnings.
- `dotnet ef migrations add InitialCreate` — succeeds, **no multiple-cascade-path errors**.
- `dotnet ef migrations script` — 15 tables; all CHECK constraints, covering indexes
  (`CompanyConnections`, `CompanyConnectionScopes`), filtered indexes (`OtpChallenges`, `UserSessions`),
  unique indexes, and `rowversion` present (see `initial-schema.sql`).
- `dotnet ef migrations has-pending-model-changes` — clean (snapshot matches model).
- `Down` drops all 15 tables — reversible.

> Note: model + migration **generation** are verified. Applying against a live SQL Server
> (`database update`) was not run — no SQL Server instance is provisioned in this environment. That step
> belongs to QA / first real deploy.
