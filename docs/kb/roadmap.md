# Roadmap

Goal: make VaM faster through lean, reliable symlink profiles, without rewriting var files.

| Release | Focus |
|---|---|
| 0 | Measure, fix the build, safety bugs, fast lookups, tests, plugin fixes, safe migration |
| 1 | Reliable dependencies: `.minN`, case-insensitive matching, resolution status, deep scan, scan cache, per-file details |
| 2 | Lean profiles: create from selection, direct vs dependency tracking, keep-enabled base set, preview, orphan cleanup, undo, health check, launch with profile |
| 3 | Library health and storage: storage and unused reports, cleanup suggestions, issues view, corruption and duplicate checks |
| 4 | SQLite, .NET 10, parallel scanning, background thumbnails, drop permanent admin if possible |
| 5 | Browsing: search filters, labels, `.hide` and `.fav` sync, content gallery, dependency graph |
| 6 | Hub downloads: parallel dependencies, sizes and cancel, search by exact package name |

## Release status

Releases 0 to 2 are built. Migrating a real library and using a profile in VaM are still to do.

## Migration

Built and tested on a small copy of real vars: preview, move, Update DB, and undo with file hashes unchanged. It stores `.var.disabled` content as unlinked vars, keeps companion files beside their vars, and refuses a repository on a different drive. Migrating a real library is still a separate decision.

## Decisions

- varManager is the only owner of the library.
- Vars are never rewritten. No editing, repackaging or in-place deduplication.
- Enable and disable through links, not by moving files.
- Built-in downloads use the VaM Hub only.
- No AI features, LAN mode or Electron rewrite.
- VaM internals aren't patched. VPB can be tested alongside varManager later. It is GPL-3.0, so reuse ideas only.
- Upstream `feelfar/varManager` shows no license on GitHub. Ask its author before publishing builds.
