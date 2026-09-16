---
name: offline-master-data-sync-feature-history
description: Round-by-round history of the "Offline Master-Data Retrieval and SQLite Synchronization" feature review (backend 5 listing endpoints + mobile sqflite sync engine).
metadata:
  type: project
---

Round 1 REJECT (2026-09-15): Backend side (IAMS-backend) was excellent — five vertical slices
(`Features/MasterData/List{Companies,Locations,Warehouses,Racks,Bins}`), the `SyncCursor`
encode/decode, the peek-ahead `MasterDataPaging.BuildPage`, the `AccessCheckService.
GetReachableCompanyIdsAsync` addition, the generated `SyncCursorUtc` column + composite indexes,
and the `BadRequestExceptionHandler` were all traced by hand and found correct: malformed-cursor
400 fix confirmed present and consistent across all 5 handlers (`TryDecode` bool checked
everywhere); reachable-company filtering rides the denormalized `CompanyId` and can't be bypassed
via a crafted cursor/parentId combo; keyset pagination tie-breaks on `Id` correctly (verified via
`MasterDataSyncSqlIntegrationTests.KeysetPagination_MatchesFullOrderedScan_StableAndTieBroken`,
which deliberately seeds a `SyncCursorUtc` tie and pages with `pageSize=2` so the boundary falls
inside the tie pair); `AccessCheckService.EvaluateAsync`/`GetActorPolicyVersionAsync` untouched
(confirmed via `git diff`, purely additive). `dotnet build` 0/0, `dotnet test` 73/73 passed
(InMemory-provider subset only — `IAMS_PG_TEST_CONN` was unset in-session, so the real-Postgres
integration tests weren't independently executed, only traced).

**The reject was 100% mobile-side, and it's a build-breaking one**: `sqflite`/`path` were never
added to `IAMS-mobile/pubspec.yaml` despite the plan explicitly mandating it and the code
(`lib/core/storage/app_database.dart`, `lib/features/masterdata/data/hierarchy_local_data_source.dart`)
importing `package:sqflite/sqflite.dart`. `git diff HEAD -- pubspec.yaml` was empty; `pubspec.lock`
had no sqflite entry. Independently running `flutter analyze` (23 issues, real compile errors —
"Target of URI doesn't exist: 'package:sqflite/sqflite.dart'", "Undefined class 'Database'") and
`flutter test` (5 test files failed to even *load*) proved this — and critically, the failure
**regressed a previously-passing, unrelated test** (`test/widget/activation_screen_test.dart`),
because `service_locator.dart` now transitively imports the broken `app_database.dart`. This is
not a live-device/emulator limitation (QA's documented, legitimate Xcode-toolchain gap, see
[[mobile-emulator-environment]] in the qa-engineer memory) — `flutter analyze`/`flutter test`
need no simulator at all, so QA's "mobile code-level-verified" characterization was materially
wrong on this specific point; the omission would have been caught by the most basic non-live check.

Secondary finding (MEDIUM, not blocking): the reachable-company snapshot diff in
`hierarchy_sync_service.dart` correctly *forces a full re-pull* of children when the reachable set
changes, but there is **no actual row-deletion/pruning logic anywhere** in the mobile codebase for
a company (or its descendants) that becomes unreachable (grep for `delete`/`DELETE`/`prune` in
`lib/features/masterdata/` turns up nothing except the snapshot table's own replace). The backend
contract doc's wording ("the client keeps its now-stale local rows **until it prunes** by the same
snapshot diff") reads as a promise that pruning happens; it doesn't — rows just sit in SQLite
forever once synced. Low practical blast radius *today* only because `HierarchyRepository` has zero
UI consumers yet (grep confirms only DI registration + its own test file reference it) — but this
will matter the moment a browsing screen is built on top of it, and it's a genuine at-rest data
retention gap independent of any UI (revoked `CompanyConnection` ≠ purged device data).

**How to apply:** on any resumed round of this feature, first re-run `flutter pub get` + `flutter
analyze` + `flutter test` yourself before trusting any "mobile looks fine" claim — this is the
single fastest way to catch a missing/misdeclared pubspec dependency, and per
[[verify-build-test-claims-independently]] don't take a teammate's or QA's narrative as a substitute.
Second, once sqflite is actually installed, re-verify the full mobile test suite (not just that it
now compiles) since the hand-written `hierarchy_sync_service_test.dart` test design was strong when
read statically (covers partial-failure/resume, reachable-set diff, empty-page cursor preservation)
but was never actually confirmed green. Third, decide with the Lead whether the no-pruning gap needs
a follow-up ticket before any master-data browsing UI is built, and if so ask that the contract doc's
"until it prunes" wording be corrected to state plainly that pruning is NOT yet implemented.
