---
name: verify
description: Drive the real varManager WinForms desktop app against a throwaway fake VaM install and prove first-run behaviour. Builds Release, launches a temp copy, checks the migration preview, cancels, confirms on seed data, and runs Update DB. Use to confirm a change to startup, migration, Update DB, tidying, linking, or the database still works in the real app.
---

# Verify varManager

One scripted smoke test of the real app. It covers the first-run path only, and [features/README.md](features/README.md) says what it does not cover.

**Safety rule:** never point this at a real VaM install, and never click `Move files` on a real library. Everything the script touches lives in a per-run sandbox. It reads the paths from the live preview dialog and refuses to click `Move files` unless they are inside that sandbox. Do not remove that guard.

## Doctor

Run this first, and whenever anything looks off. It is read-only, and it exits with the number of failed checks.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\verify\scripts\smoke.ps1 -Doctor
```

It checks for 64-bit PowerShell, the ACE OLEDB 12.0 provider, MSBuild and a built exe, and that VaM is not running. It warns about other `varManager` processes and leftover sandboxes.

If the ACE provider or MSBuild is missing, stop and tell the user. Do not install anything system-wide without asking.

## Launch and drive

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\verify\scripts\smoke.ps1
```

Add `-NoBuild` to test the existing `varManager\bin\Release`. `-TimeoutSeconds <n>` limits the wait for Update DB (default 120). A run takes about 10 seconds plus the build. Exit code 0 means every check passed.

The script does these steps in order:

1. Builds Release with MSBuild.
2. Copies `bin\Release` to `%TEMP%\varManager-verify\run\<stamp>\app`, leaving out any `varManager.log`, `varsForInstall.txt` and `varsMigrated.txt`.
3. Sets `varspath` and `vampath` in the copied `varManager.exe.config` to fake folders in the sandbox.
4. Creates a fake `VaM.exe`, then seeds a real `AddonPackages` folder with `Smoke.Alpha.1.var`, `Smoke.Beta.1.var.disabled` and `readme.txt`.
5. Launches with `__COMPAT_LAYER=RunAsInvoker`, so no elevation is needed. The working directory is the app copy, because `varsMigrated.txt`, `varsForInstall.txt` and `varManager.log` are resolved against it.
6. Reads the preview, clicks `Cancel`, and proves nothing moved.
7. Relaunches, clicks `Move files` on the seed data, then runs `UPD_DB`.
8. Closes the app, checks the files, link and database, and removes the sandbox.

Ready means the expected window exists. The script finds windows by title, and buttons by text under the app's process ID, then posts `BM_CLICK` to the handle. UI Automation sees these buttons as panes with no Invoke pattern, so it does not work here.

| Window | Title | Buttons |
|---|---|---|
| Migration preview | `Move library into varManager` | `Cancel`, `Move files`, `Save plan...` |
| Main form | `VarManager  v<version>` | `UPD_DB` |
| Update DB confirmation | `UpdateDB` | `&Yes`, `&No` |
| Migration result | empty, class `#32770`, text starts `Files moved` | `OK` |

## Evidence

Each run writes to `%TEMP%\varManager-verify\evidence\<stamp>\`, which survives cleanup.

- `result.json`: verdict, commit, and every check by name. `commit` is `HEAD`, so uncommitted edits in the build are not recorded.
- `steps.log`: timed transcript of the run.
- `build.log`: MSBuild output. It is absent with `-NoBuild`.
- `preview-cancel.txt`, `preview-confirm.txt`: the dialog text as shown, with PNGs of the same name.
- `updatedb-confirm.png`, `migration-result.png`, `main-after-updatedb.png`: screenshots of the user path.
- `vars-table.txt`: the `vars` table read from the `.mdb` after the app closed.
- `tree.txt`, `migration-*.tsv`, `varManager.log`, `varManager.exe.config`: final sandbox listing, the move log, the app log, and the config used. Check that config if a run seems to hit the wrong folders.
- `failure-*.png`, `unexpected-dialog.png`: open windows when a run failed.

Proof standards:

- A change is proven only by a run that built it. A `-NoBuild` run proves whatever is in `bin\Release`.
- Report side effects from the checks and files above, not from the screenshots alone.
- Add new checks to the journey for a change the smoke test does not reach. Never weaken a check to get a pass. If a check fails, report it with the evidence path.

## Cleanup

The script kills only the process it started and removes its own sandbox, on pass and on failure. If a run was interrupted, remove leftovers with this command. It never touches evidence.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\verify\scripts\smoke.ps1 -Cleanup
```

An interrupted run can leave a `varManager` process behind. Stop it by ID only after its path shows it is under `%TEMP%\varManager-verify\run`. Never stop `varManager` by name, since another session may own it.

Delete sandboxes with `cmd /c rmdir /s /q`, as the script does. `Remove-Item -Recurse` in PowerShell 5.1 can follow directory symlinks. Old evidence folders are left for you to delete.

## Gotchas

- The script builds the working tree in the shared `varManager\bin` and `obj` folders. If another session is editing in this checkout, the build includes its unfinished work and can fail on it. Two builds at once can collide. Check `git status` and use `-NoBuild` if the build is not yours to test.
- Seed vars need a `meta.json` entry. Without it, Update DB moves them to `___VarnotComplyRule___`.
- Symlink creation without elevation depends on Windows Developer Mode. Without it, the app fails to create links and the run fails at `AddonPackages is now a symlink`.
- Vars and VaM must share a drive, or `Move files` is disabled. The sandbox keeps both under `%TEMP%`.
- `RescanPackages` does nothing here, because it runs only while VaM is running. Update DB has no completion dialog either. The script treats it as finished when the link exists and `varsMigrated.txt` is gone.
- A running VaM makes startup refuse migration, so the script stops first.
- `FormSettings` opens if `VaM.exe` is not found, and the run then times out waiting for the preview. Check `varManager.exe.config` in the evidence.
