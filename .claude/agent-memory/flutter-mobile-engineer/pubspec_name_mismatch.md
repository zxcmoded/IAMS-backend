---
name: pubspec-name-mismatch
description: IAMS-mobile/pubspec.yaml locally has name changed from iams_mobile to giso, breaking flutter analyze/test repo-wide
metadata:
  type: project
---

The working tree at `IAMS-mobile/pubspec.yaml` has had `name: iams_mobile` changed to
`name: giso` (description also updated to "IAMS Project Mobile App") as an uncommitted local
edit, present before any of my sessions touched the repo. Every `.dart` file in `lib/` and
`test/` still imports via `package:iams_mobile/...`, so with `name: giso` in pubspec.yaml,
`flutter analyze` and `flutter test` both fail on nearly everything (`Target of URI doesn't
exist`), even though the code itself is fine.

**Why:** Unclear — looks like an in-progress rename to "giso" that never got the accompanying
`s/package:iams_mobile/package:giso/` sweep across `lib/`/`test/`, or an accidental edit. It is
uncommitted (shows in `git status` as modified, `git diff` confirms the two-line change), so it
predates and is unrelated to any feature work.

**How to apply:** Before trusting a `flutter analyze`/`flutter test` failure count as a
regression from your own change, check `git diff pubspec.yaml` — if `name:` is `giso` instead of
`iams_mobile`, that's this pre-existing local drift, not something you broke. To actually run
analyze/tests, temporarily flip `name:` back to `iams_mobile` (do not touch `description:`),
`flutter pub get`, run, then restore pubspec.yaml to exactly what `git diff` showed before you
touched it and `flutter pub get` again — don't leave the repo in the `iams_mobile` state, since
that's not your change to make permanent. Do not attempt the repo-wide `giso` import rename
yourself unless the user explicitly asks for it — flag it back to them instead, since it's a
call about the app's actual product name.
