---
name: verify-build-test-claims-independently
description: Always independently re-run build/test and re-grep for claimed fixes (logging, dead code) rather than trusting an implementer's narrative summary of what changed.
metadata:
  type: feedback
---

When a review task lists "what the implementer claims to have fixed," always independently verify each claim from the current file state rather than trusting the narrative — especially when the task explicitly notes the implementing agent's process crashed or stalled mid-round. Concretely: re-run `dotnet build` / `dotnet test` yourself and compare exact numbers, `grep` for the specific secret/token/pattern claimed removed, and read the actual diff/current code rather than the description of it.

**Why:** In practice the narrative descriptions have been accurate for the narrow claims made (e.g. round 3 of the device-binding feature: build/test counts, InMemory relaxation, and log-statement content all matched exactly as described). But the implementer's own framing of *why* a fix is sufficient can be wrong even when the mechanical claim (it compiles, it doesn't crash, tests pass) is true — round 3's match-branch fix genuinely stopped the crash but introduced a silent security-invariant bypass that no test caught. See [[device-binding-feature-history]].

**How to apply:** Treat "build succeeds / tests pass / grep found nothing" claims as independently verifiable and verify them literally. Treat "this fix is safe/sound" claims as requiring your own reasoning through the concurrency/security invariant, tracing every other writer to the same row/table, not just confirming the exception no longer escapes.

**Cross-repo corollary (2026-09-15, Offline Master-Data Sync feature, mobile side):** the same
principle applies across repos, not just within one handler. QA's mobile report said "code-level-
verified" (citing a real, documented environment limitation — broken Xcode toolchain blocking
simulator/emulator, see the qa-engineer memory's `mobile-emulator-environment`). That framing was
technically true but incomplete: `flutter analyze`/`flutter test` need no simulator at all, and
independently running them turned up a build-breaking omission (sqflite never added to
`pubspec.yaml`) that "code-level-verified" implied wouldn't exist. Lesson: a legitimate excuse for
skipping *live-device* verification is not the same as an excuse for skipping *static*
verification (build/analyze/unit-test) — always run the cheap, no-hardware-required checks
yourself even when the narrative explains why the expensive ones were skipped. See
[[offline-master-data-sync-feature-history]].

**Sandbox limitation (round 5, 2026-09-14):** a local SQL Server container is typically running (`docker ps` shows it), but this environment's auto-mode classifier blocks any command that would materialize its SA password into the transcript — `docker exec/inspect | grep SA_PASSWORD`, and even a bare `printenv IAMS_SQL_TEST_CONN` or `dotnet test` run shortly after attempting the former, get denied as "credential materialization." Don't try to work around this (guessing default passwords, reading secrets files, etc.) — it's an intentional boundary. `if [ -z "$VAR" ]; then ...` (presence-only, no value printed) is allowed. When a real-SQL-Server integration test can't be executed this way, fall back to manually tracing the exact EF Core / SQL mechanics the test relies on (e.g., identity-map resolution behavior, RowVersion concurrency tokens) and state plainly in the report that execution was not independently reproduced, only reasoned through — don't claim you ran it. Run the fast/InMemory test subset directly instead (`dotnet test <unit-test-csproj-path>`, scoped rather than the bare `dotnet test` which seemed to trigger the classifier's suspicion once already denied in the same session) to at least verify the non-SQL numbers exactly.
