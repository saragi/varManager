# First-run smoke test

A new user starts varManager with a real `AddonPackages` folder. The app shows what it will move, moves nothing on Cancel, and moves files only on confirmation. Update DB then indexes the vars and links the enabled ones back.

## Sub-features

- `preview`: the dialog shows the right paths and counts before anything moves.
- `cancel`: Cancel closes the app and changes no files.
- `migrate`: `Move files` stores the vars, writes a log, and makes `AddonPackages` a symlink.
- `update-db`: `UPD_DB` tidies vars, fills the database, and links enabled vars.
- `lean-profile`: `Lean Profile` builds a links-only switch from the selected row, and `Profile Check` reports on it.

## How to get to it (user POV)

- `preview`, `cancel` and `migrate`: start the app with `AddonPackages` as a real folder that has content. The preview opens before the main window.
- `update-db`: click `UPD_DB` on the main window and answer Yes. This is the only entry point the journey drives.

## Driving it with smoke.ps1

Preconditions: doctor passes and VaM is not running. The script creates all seed data.

- Preview: launch the app. The dialog names the sandbox paths and counts 1 var, 1 disabled var and 1 other file.
- Cancel: click `Cancel`. The process exits, the VaM folder listing is identical, `AddonPackages` is a real folder, and the vars folder is empty.
- Migrate: relaunch and click `Move files`. The result box starts `Files moved`, `AddonPackages` is a reparse point, one `.tsv` is in `___MigrationLogs___`, and `varsForInstall.txt` lists `Smoke.Alpha.1`.
- Update DB: click `UPD_DB`, then `&Yes`. `___VarTidied___\Smoke\Smoke.Alpha.1.var` has the original hash, and `___VarsLink___\Smoke.Alpha.1.var` is a symlink to it. The `dependencies` table has the scene-only reference `Deep Creator.Hidden_Pack.min2`, and `Smoke.Alpha.1` does not depend on itself.
- Changed var: replace the tidied var with a file of a different size and run `UPD_DB` again, retrying on the `still running` box. The `dependencies` table gains the new references and loses the old one.
- Stored state: after the app closes, the `vars` table has `Smoke.Alpha.1` with `scenes = 1`, and `Smoke.Beta.1`. Beta has no link, `readme.txt` is still reachable through `AddonPackages`, and `varManager.log` has no `ERROR` or `FATAL`. `Smoke.Beta.1` depends on `Smoke.Alpha.1`.

- Lean profile: runs after the stored-state checks, so `AddonPackages` ends the run pointing at `lean`. Relaunch, click the first row header, click `Lean Profile`, then `Create profile`. `___Profiles___\lean.profile` records `Smoke.Alpha.1`, the `lean` switch holds only links, and the var hash is unchanged. `Profile Check` lists the missing packages.
- Repair and restore: delete a lean link and `Repair` brings it back. Create a version from the second row, then `Restore previous` brings the first back and removes the extra link. The app log still has no errors.

## Gotchas

- The startup job can hold the background worker when `UPD_DB` is clicked. The script answers the `still running` box and retries.
- The script finds the var list by the control under a point in the main window, because UI Automation exposes no IDs here. A layout, window size or display scale change can break that lookup and the row clicks.
- The seed scene holds only package references. It exercises the dependency deep scan, but not real scene parsing or previews.
- The tree listing shows the switch folder in lower case (`___addonpacksswitch ___`), because `Form1_Load` lowers the path. That is existing behaviour.
