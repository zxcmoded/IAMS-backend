# IAMS Backend — Schema Design (Phase 1)

**Scope:** Activation Key Authentication (superseding F1's original username/password + 2FA/OTP) and
F15 (Parent/Child Tenant Connections).
**Status:** Design + verified EF Core model, targeting PostgreSQL. The real model in `src/IAMS.Api`
compiles against EF Core 10 / Npgsql; `dotnet ef migrations script` (both migrations chained) generates
cleanly and is reversible.

> **2026-09-14 — Activation Key Authentication replaced username/password + 2FA/OTP:** `OtpChallenges`,
> `UserTwoFactorSettings`, and the old per-user `UserDeviceBindings` table were **dropped**
> (migration `ActivationKeyAuthentication`). `Users` gained `ActivationKeyHash` (unique, SHA-256 hash of
> the Activation Key — the only credential now), `ActivationStatus`, `ActivatedDeviceId`,
> `ActivatedAtUtc`, `ActivationResetAtUtc`, `ActivationResetByUserId`. `Users.NormalizedUsername` and
> `Users.PasswordHash` were dropped; `Username`/`Email` remain as display/contact-only fields. The
> `xmin`-backed optimistic-concurrency token that used to guard `UserDeviceBindings` now guards `Users`
> directly (see §2.4/§4). See `docs/api/activation-key-authentication.md` for the new endpoint contract;
> `docs/api/F1-F15-auth-and-tenant-connections.md` and `docs/api/single-device-user-access.md` are
> historical records of the superseded login/2FA/device-binding contract and are **not** updated in place.
>
> **Active company is per-session:** resolved from `UserSessions.ActiveCompanyId` (+ optional
> `ActiveLocationId`), **not** a column on `User`. A user can be a member of multiple companies
> (`UserCompanyMemberships`); which one is active is session state, so `GET /me/scope` reads it off
> the session record. Cross-tenant connection scope is resolved dynamically per request (not frozen
> in the session) so policy changes are honored without re-issuing the session.

> **2026-09-22 — Tenancy & access model restructured; Tenant + cross-company connections removed:**
> `Tenants`, `CompanyConnections`, `CompanyConnectionScopes`, `CompanyConnectionFilters`, `Roles`, and
> `UserCompanyMemberships` were **dropped** (migration `RemoveTenantAndCompanyConnections`), along with the
> `TenantId` FK/denormalized-ancestry column on `Companies` and every hierarchy/inventory table below it, and
> `UserSessions.ActiveCompanyId`/`ActiveLocationId`. **`Company` is now the top-level unit.** `Users` gained a
> non-nullable **`CompanyId`** FK (a user belongs to exactly one Company) and an int-coded **`Role`** column
> (`SuperAdmin=800, Admin=700, Manager=300, User=200, Viewer=100`, guarded by `CK_Users_Role`; **not** a lookup
> table). A new **`UserLocationAssignments`** join table (unique `(UserId, LocationId)`) models the
> many-to-many user↔Location assignment; data access is scoped to a user's Company and — for the
> location-restricted roles (Manager/User/Viewer) — their assigned Locations. The `PolicyRevision`/`xmin`
> machinery specific to connections is gone; the `xmin` token on `Users` and the inventory rows, and the
> `SyncCursorUtc` generated columns, are unchanged. See `docs/api/tenancy-and-access-model.md` for the API
> contract. **The `reference-model/` C# files below describe the pre-2026-09-22 (Tenant/connection) design and
> are historical — the live model in `src/IAMS.Api` is the source of truth.** `Sections 1–7 below likewise
> predate this change and are retained for historical context.`

This document establishes the PostgreSQL / EF Core (Npgsql) conventions the `dotnet-backend-engineer`
should build the persistence layer on. `initial-schema.sql` is the generated DDL for review.

---

## 1. What this covers

| Area | Tables |
|---|---|
| Physical hierarchy | `Tenants`, `Companies`, `Locations`, `Warehouses`, `Racks`, `Bins` |
| Cross-tenant connections | `CompanyConnections`, `CompanyConnectionScopes`, `CompanyConnectionFilters` |
| Identity / auth | `Users` (Activation Key + device-binding fields), `Roles`, `UserCompanyMemberships`, `UserSessions` |

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
- **Optimistic concurrency on admin config edits** rides Postgres's `xmin` system column (mapped as a
  shadow "row version" property in `IamsDbContext`; there is no `rowversion` column) — distinct concern
  from `PolicyRevision`, which is the semantic, client-facing version. The same `xmin` technique is also
  applied to `Users` (see the 2026-09-14 callout above) to make Activation Key device-binding atomic:
  two concurrent activation attempts on the same never-activated key are both plain UPDATEs to the same
  row, and `xmin` turns the loser's write into a catchable concurrency conflict instead of a silent
  double-bound key.

### 2.5 Permission level: connection default + optional per-scope override
`PermissionLevel` (Read/Write/Full) is required on the connection. `CompanyConnectionScopes` has a
**nullable** `PermissionLevelOverride`. This deliberately keeps the unresolved gaps open: whether
permissions inherit down the hierarchy or are set per level, and whether a role further restricts them,
can both be resolved later without a schema change.

### 2.6 GUID primary keys
All PKs are `uuid`. EF Core generates **sequential** GUIDs client-side, which keeps clustered
indexes from fragmenting. Rationale: multi-tenant isolation, safe id generation across environments, and
forward-compatibility with offline/mobile-generated ids in later phases (F11 sync).

### 2.7 Enums as strings + CHECK constraints
Enums persist as `character varying(20)` via `HasConversion<string>()`, each backed by a `CHECK … IN (…)`
constraint. Strings are self-documenting in the DB and immune to the "someone reordered the enum" class
of bug that int-backed enums invite. The CHECK makes an out-of-domain value **unrepresentable**.

### 2.8 `DeleteBehavior.Restrict` / `NoAction` across hierarchy & connections
Because ancestry is denormalized, several FKs point at the same ancestor table — under SQL Server this
combination produced "multiple cascade paths" errors under cascade; Postgres doesn't have that specific
restriction, but the schema keeps `Restrict`/`NoAction` on all hierarchy and connection FKs anyway, for
one consistent deletion story rather than a mix of DB-cascade and soft-delete. Deletion is handled via
**soft-delete (`IsActive`)** and explicit application logic. Only genuinely user-owned children cascade
(`OtpChallenges`, `UserSessions`, `UserCompanyMemberships`, `UserTwoFactorSettings`,
`CompanyConnectionScopes`/`Filters` off their connection).

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

- **Target:** .NET 10 / EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL` `10.0.x`). This is what the
  real model in `src/IAMS.Api` is verified against; change deliberately, not by accident.
- **Entities** live in the domain layer; **configuration** in `IEntityTypeConfiguration<T>` classes
  registered via `modelBuilder.ApplyConfigurationsFromAssembly(...)` — never inline in `OnModelCreating`.
- **Enums:** `HasConversion<string>()` + `HasMaxLength(20)` + a `CHECK … IN (…)` (see the `AddEnumCheck`
  helper in `HierarchyConfigurations.cs`). Raw check-constraint/filter SQL must use Postgres
  double-quoted identifiers (`"ColumnName"`), not SQL Server's `[ColumnName]` brackets.
- **Timestamps:** UTC only (`…AtUtc`); every `DateTime` property maps to `timestamptz` via a
  `ConfigureConventions` override in `IamsDbContext`, and `CreatedAtUtc` defaults to `now()` at the DB.
- **Deletes:** `Restrict`/`NoAction` on cross-aggregate FKs; soft-delete via `IsActive`.
- **Optimistic concurrency:** Postgres has no `rowversion` equivalent. Where SQL Server used
  `.IsRowVersion()` on a `byte[]` column, the Postgres model instead maps the `xmin` system column as a
  shadow `uint` "row version" property (`.Property<uint>("xmin").ValueGeneratedOnAddOrUpdate().IsRowVersion()`),
  applied conditionally in `IamsDbContext.OnModelCreating` since `xmin` doesn't exist on the in-memory test
  provider.
- **Migrations:** generate with `dotnet ef migrations add <Name>` into `Persistence/Migrations`. Name them
  in PascalCase describing the change (`InitialCreate`, `AddAssetRegister`, …). Do **not** hand-edit the
  designer/snapshot files; do review the generated `Up`/`Down` SQL before committing.
- The migration history table is `__EFMigrationsHistory` (default).

---

## 5. Migration safety & rollback

- **Initial migration on a greenfield DB:** no existing rows, so **no backfill and no
  default-value-for-new-non-null-column concerns**. Pure `CREATE`.
- **Reversible:** the `Down` drops all 16 tables (verified). On a populated DB a rollback is destructive
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

Originally performed against EF Core 10 + SQL Server provider; re-verified 2026-09-14 against EF Core 10
+ Npgsql (PostgreSQL) after the engine migration. Re-verified again the same day after the Activation Key
Authentication migration, and once more after a code-review pass fixed a CRITICAL migration-safety bug
(see the callout below):
- `dotnet build` (whole solution, API + tests) — succeeds, 0 warnings.
- `dotnet test` — all 50 default (non-opt-in) tests pass.
- `dotnet ef migrations add ActivationKeyAuthentication` — succeeds; drops `OtpChallenges`,
  `UserDeviceBindings`, `UserTwoFactorSettings`, adds the Activation Key/device-binding columns + `xmin`
  onto `Users`.
- `dotnet ef migrations has-pending-model-changes` — clean (snapshot matches model).
- **`dotnet ef database update` run for real** against a local Postgres (`InitialCreate` then
  `ActivationKeyAuthentication` applied in sequence) — succeeds; resulting `Users` table schema inspected
  directly via `psql` and matches the model (unique index on `ActivationKeyHash`, `CK_Users_ActivationStatus`
  check constraint, FK to self on `ActivationResetByUserId`).

> **Migration-safety fix (code review, same day):** the first version of `ActivationKeyAuthentication`
> added `ActivationKeyHash`/`ActivationStatus` as `NOT NULL` with a single shared literal default (`""`)
> in the same statement as their `UNIQUE` index / `CHECK` constraint. Reproduced live: apply
> `InitialCreate`, insert one plain `Users` row (simulating a dev/staging/demo DB that has ever run the
> old password/2FA schema), then `dotnet ef database update` — failed with Postgres `23514` (check
> constraint violated by an existing row) and rolled back entirely; with ≥2 pre-existing rows it would
> additionally have failed with `23505` on the unique index (every row sharing the same `""`). Fixed by
> adding both columns **nullable** first, backfilling with `UPDATE` (each legacy row gets a
> `'legacy:<its own Id>'` placeholder hash — guaranteed unique, and shaped nothing like a real 64-hex-char
> SHA-256 hash — plus `'NotActivated'`), THEN tightening to `NOT NULL` and adding the unique
> index/`CHECK` constraint. Re-reproduced with 1 row and again with 3 pre-existing rows after the fix —
> both apply cleanly now.
- `dotnet ef migrations script` — regenerated `initial-schema.sql` from both migrations chained (see that
  file); reversible (`Down` for `ActivationKeyAuthentication` re-creates the three dropped tables and
  restores the two dropped `Users` columns).
- The opt-in `ActivationSqlConcurrencyTests` (replacing the old `DeviceBindingSqlConcurrencyTests`) were
  run for real against that same local Postgres via `IAMS_PG_TEST_CONN` — all 3 scenarios (fresh-key race,
  post-reset race, same-device double-tap) pass, proving the `xmin`-backed atomicity on `Users` actually
  holds under real concurrent connections, not just in the InMemory-provider unit tests.
