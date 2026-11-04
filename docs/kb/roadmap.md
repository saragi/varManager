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

Releases 0 to 2 are built, and the real library is migrated. Using a lean profile in VaM is still to do.

## Migration

Built and tested on a small copy of real vars: preview, move, Update DB, and undo with file hashes unchanged. It stores `.var.disabled` content as unlinked vars, keeps companion files beside their vars, and refuses a repository on a different drive. The real migration moved 5,945 vars and took 106 minutes, nearly all of it the first Update DB.

## Redesign

Releases 4 and 5 together deliver a new app. The direction is approved in the [clickable mockup](https://claude.ai/artifact/2ugrax4vYy63wpfpRVBKWH), which is private to the owner and uses sample data. No app code has started.

Build it as a WPF app on .NET 10, beside the old app, which stays usable until the new one covers everything. Qvaro and VaM Backstage are layout inspiration only, with no code or art reused.

Start after the real migration and real lean-profile use, because daily use may change what the app needs. Then:

1. Measure profile switch time on the real library.
2. Move logic out of `Form1.cs` into a tested library, keeping the old app working.
3. Make scanning automatic and background.
4. Move the database to SQLite.
5. Build the screens one at a time.

### Screens

| Screen | Purpose |
|---|---|
| Library | Thumbnail gallery with a detail panel showing dependencies |
| Scenes | Shows whether each scene is ready in the current profile, or what is missing |
| Profiles | Build a lean profile from picks, compare timings, switch |
| Health | Problems first, nothing fixed without asking |
| Missing | Everything the library needs, grouped by what fixes it |

The profile switcher, Update DB and Launch VaM are always visible. A job bar at the bottom replaces popups. Turning a package on shows how many dependencies come with it first.

### Library search

- There is no separate author view. The search box suggests matching authors, up to 6 at a time.
- A clicked author becomes a removable tag. Enter picks the first suggestion, and Backspace in an empty box removes the last tag.
- Several author tags match any of those authors, and results then appear under collapsible author headers.
- Typed text and the type and status filters narrow the result further.

### Move out

Move out takes a package and the dependencies only it uses out of the library.

- It shows what moves and what stays before anything happens.
- It is blocked while other vars need the package itself.
- A dependency stays if any remaining var needs it, including disabled vars, or if a profile, `base.txt` or a scene in `Saves` uses it.
- Files go to a holding folder with undo. Moving frees no disk space, and deleting from the holding folder is a separate confirmed step.

### Missing

Missing replaces a separate Downloads screen. The rule is as few clicks as possible: the app does the checking, and the user only makes real choices.

- The check runs by itself after every library change, from the database first and then the Hub. The side nav shows the count.
- One click on `Get all` downloads every Hub package, scans it and links it to the current profile. Work continues in the job bar.
- `Fix` on a scene or profile does the same for just that scene or profile, and turns on stored vars it needs. No screen opens unless something is left over.
- Nothing is downloaded when an owned version satisfies the reference, and an older version is never fetched over a newer one.
- Updates are never automatic, because a new version can change a scene.
- Hub login is asked once and remembered, if the Hub needs it. This is not checked yet.
- It only stops to ask when the download won't fit on the disk.
- The app can read names in a local SharpVaMTools install to show which packages it lists, and `Copy list` exports them. The app doesn't download from mirrors, and that data stays out of this repo.
- Files added to the repository by hand are found and scanned automatically.

### Performance rules

- Open var contents once, when a var enters the library, in the background.
- Never scan at profile switch or launch. Switching repoints `AddonPackages` and refreshes link status.
- Plan profiles from the database, without opening var files.
- Load only the thumbnails on screen. The library has over 53,000 preview images.
- VaM's own startup scan stays inside VaM, so only fewer links make it faster.

## Decisions

- varManager is the only owner of the library.
- Vars are never rewritten. No editing, repackaging or in-place deduplication.
- Enable and disable through links, not by moving files.
- Built-in downloads use the VaM Hub only.
- No AI features, LAN mode or Electron rewrite.
- VaM internals aren't patched. VPB can be tested alongside varManager later. It is GPL-3.0, so reuse ideas only.
- Upstream `feelfar/varManager` shows no license on GitHub. Ask its author before publishing builds.
