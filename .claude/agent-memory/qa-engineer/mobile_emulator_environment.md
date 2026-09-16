---
name: mobile-emulator-environment
description: This machine's Xcode/iOS toolchain is broken, blocking both simulator boot and any native-binary flutter dev-dependency (e.g. sqflite_common_ffi) — confirmed root cause of a prior QA session's emulator hang, time-box it instead of retrying
metadata:
  type: project
---

Confirmed 2026-09-15: this machine's Xcode install is broken at the `xcodebuild`/`lipo`/`simctl`
level — `xcrun simctl list devices booted` fails immediately with `dlopen(@rpath/
libxcodebuildLoader.dylib...): Symbol not found: _XPCTypeBool`, and the same underlying failure
also breaks `flutter pub add --dev sqflite_common_ffi` at build time (`sqlite3` package needs
`lipo` to build a universal binary on macOS). No `emulator`/`adb` CLI is installed either (no
Android SDK on PATH), so there is no path to a live Android emulator here either.

**Why this matters:** a prior QA session on this same machine got stuck in a long wait loop trying
to boot an Android emulator and never delivered a report, burning a large budget. The Team Lead
explicitly flagged this and asked future QA runs to time-box any device/emulator step to a few
minutes. This memory explains *why* it will keep failing here specifically — it's an environment
defect, not a transient/flaky boot, so retrying or waiting longer will not help.

**How to apply:** for IAMS-mobile QA work on this machine, check `xcrun simctl list devices booted`
and `adb devices`/`emulator -list-avds` up front (each returns in seconds, no boot attempt) — if
both come back empty/erroring, do not attempt to boot a simulator/emulator or add any dev-dependency
that requires compiling a native binary (sqlite3, ffi packages, etc.). Fall back immediately to
`flutter test` + `flutter analyze` + rigorous code-path tracing, and say explicitly in the report
which findings are code-level-verified vs. live-device-verified. If genuinely live-device
verification is required, flag to the user/Lead that this machine's toolchain needs repair first
rather than spending budget retrying.

See also [[real-db-verification-approach]] for the equivalent "verify without live device" pattern
already established for backend/Postgres work.
