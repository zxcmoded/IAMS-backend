---
name: postgres-migration-context
description: IAMS-backend switched from SQL Server to PostgreSQL (Npgsql) on 2026-09-14; QA verification approach and one real bug found in the opt-in PG integration tests
metadata:
  type: project
---

IAMS-backend's persistence layer was migrated from SQL Server to PostgreSQL (Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3) on 2026-09-14, on top of a working Phase 1 backend (auth/2FA, tenant/company/location/warehouse/rack/bin hierarchy, cross-tenant connections). Optimistic concurrency moved from SQL Server `rowversion` byte[] columns to a Postgres `xmin` shadow property (`IsRowVersion()` on a `Property<uint>("xmin")`, applied only when `Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"`). This pattern is correct and was verified working end-to-end against a real Postgres instance (see [[real-db-verification-approach]]).

**Why:** greenfield/pre-launch schema (docs/schema/README.md §5 already documented "no existing rows, pure CREATE" before the switch), so a single fresh `InitialCreate` migration replaced the old SQL Server migration history rather than a data-preserving conversion.

**How to apply:** When asked to re-verify future changes to this persistence layer, remember the baseline is now Postgres/Npgsql, not SQL Server — `SqlException`/`Microsoft.Data.SqlClient` patterns are gone; unique-violation detection is `Npgsql.PostgresException` with `SqlState == PostgresErrorCodes.UniqueViolation` ("23505"). `docs/schema/reference-model/` is intentionally left as historical SQL-Server-flavored scaffold and is NOT live convention — don't flag it as a regression.

## Known bug: opt-in PG integration tests race each other when run together

`tests/IAMS.Api.Tests/SqlPolicyRevisionIntegrationTests.cs` and `DeviceBindingSqlConcurrencyTests.cs` are both opt-in (skip unless `IAMS_PG_TEST_CONN` is set) and both hardcode the same target database name pattern in their own seed methods (`EnsureDeletedAsync` + `EnsureCreatedAsync` against whatever DB the connection string points to). xUnit v2 runs different test **classes** in parallel by default (each class is its own collection unless grouped), so running the documented command exactly as written in both files' header comments —
```
IAMS_PG_TEST_CONN="Host=localhost;Port=5432;Database=iams_qatest;Username=iams;Password=..." dotnet test
```
— reliably fails 4/59 tests every run (confirmed 3/3 runs, not flaky-random) because one class's `EnsureDeletedAsync` (a `DROP DATABASE`, which forcibly disconnects other sessions) races the other class's `EnsureCreatedAsync`/queries against the identical database. Each class passes 4/4 individually or with `--filter` scoping to one class at a time.

**Why this matters:** as more opt-in real-DB integration test classes get added to this project, this failure mode will keep recurring unless fixed once at the root (e.g. `[CollectionDefinition]` disabling parallelization between PG-backed test classes, or each class using a distinct DB name derived from the class name).

**How to apply:** when verifying "the opt-in Postgres tests pass," always run each class in isolation via `--filter FullyQualifiedName~<ClassName>` (or fix the root cause) rather than trusting a bare `IAMS_PG_TEST_CONN=... dotnet test` — the failure is real and reproducible, not a fluke of my environment.

See also [[real-db-verification-approach]] for how to get a working Postgres instance without probing container credentials.
