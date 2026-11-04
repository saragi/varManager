# Redesign handover

Working notes for finishing the new app. Read this first, then `CLAUDE.md`, `docs/kb/roadmap.md` and `docs/kb/mockup/Main.dc.html`. Update this file at the end of every step.

The clickable mockup is at https://claude.ai/artifact/2ugrax4vYy63wpfpRVBKWH. The user approved its direction. Its source is `docs/kb/mockup/Main.dc.html`.

## Where things stand

- Branch `release-0`. `master` is unchanged. Nothing has been merged.
- The real library is migrated: repository `D:\X\VarRepository`, app `D:\X\varManagerApp`, VaM `D:\X\VaM`, with `AddonPackages` a link to the `default` switch.
- The WinForms app has lean profiles, exclude profiles, Hub fixes and the Missing window. It is the app the user runs today, and it must keep working until the new app replaces it.
- Logic with no UI is in `varManager.Core`. The WinForms app links those files.
- `varManager.App` exists as an empty WPF window. It opens and closes cleanly, and doesn't use Core yet.
- `tests\varManager.App.Tests` runs the Core tests on .NET 10 too. All pass there.
- `LibraryStore` (SQLite) and `AccessDatabase.ImportOnce` exist, but nothing calls them yet. Step 4 must call the import at startup, once the app knows its paths. Add reads to `LibraryStore` as screens need them. It has only `LoadVars` and `Count` so far.
- Import proof on a copy of the real `varManager.mdb`: all 147,943 rows in 5 tables matched, it took 1.3 s, and `LoadVars` read 5,791 vars in 27 ms. The copy was deleted afterwards.

## SDK

The .NET SDK 10.0.403 is installed. If `dotnet --list-sdks` ever shows no 10.x, stop and ask the user to install it. Don't install it yourself.

Visual Studio's MSBuild still can't load SDK-style projects with the SDK installed: `MSB4236: The SDK 'Microsoft.NET.Sdk' specified could not be found`. Keep the links in `varManager.csproj`. Any new Core file needs a link there.

## Steps

Do them in order. Tick each one when it is committed, reviewed and pushed.

- [x] 0. Review commit `558c942` and the handover commit with the loop below, then push.
- [x] 1. Confirm the SDK. Create `varManager.App` (WPF, `net10.0-windows`) referencing `varManager.Core`, with a test project that can target it.
- [x] 2. Add a SQLite store to Core, with a one-time import from `varManager.mdb` that only reads the Access file. Keep the schema simple. Prove it on a copy.
- [ ] 3. Move logic out of `Form1.cs` into Core, one area per commit, keeping WinForms working. Done: Tidy (`VarTidier`, `LibraryFolders`), reading a var for Update DB (`VarScanner`), install and link (`VarLinks`, and `Comm` now lives in Core), switches and the rescan request (`VarSwitches`, `VamBridge`), scenes and previews (`SceneAnalysis`, `ScenePreviews`, load requests in `VamBridge`). Left: dependency analysis (`PrepareSaves.cs` still has its own zip code), and the Update DB loop and its database writes, which wait for the new store.
- [ ] 4. Build the WPF shell from the mockup: dark look, side nav, profile switcher, Update DB, Launch VaM, bottom job bar instead of popups.
- [ ] 5. Library: virtualized gallery that loads only on-screen thumbnails, search with author suggestions and tags, type and status filters, detail panel, Move out with undo.
- [ ] 6. Scenes: readiness per profile, one-click Fix, Load in VaM.
- [ ] 7. Profiles: lean and exclude profiles, timing cards, switch, Profile Check.
- [ ] 8. Health: stale, duplicates, disabled, storage. Nothing fixed without asking.
- [ ] 9. Missing: the same one-click flow. Add a "Not owned" Hub filter, image cache, and author search in place of the Creator dropdown.
- [ ] 10. Parity: list every WinForms button and form, and confirm each has a home in the new app or a stated reason to drop it.
- [ ] 11. Prove it on fake folders with a verify script like `smoke.ps1`. Include Fix Rebuild Link and temporary links, which `smoke.ps1` has never covered.
- [ ] 12. Stop. Tell the user it's ready and how to try it. Don't run it on the real library and don't replace `D:\X\varManagerApp` without their approval.

## Design rules from the user

- As few clicks as possible. The app does the checking, and the user only makes real choices.
- Search is one box with suggestions. Authors become removable tags, and tags combine.
- Move out takes a package and dependencies nothing else uses, to a holding folder with undo. It is blocked while other vars need the package. A dependency counts as used if any remaining var, profile, `base.txt` or scene in `Saves` needs it.
- Scanning happens once when a var enters the library, in the background. Never scan at profile switch or launch.
- Only fewer links make VaM start faster. Measured on Megan: full library 46 s startup, 24-28 s load, 10.2 GB. Lean: 24 s, 15-18 s, 4.7 GB.
- Files are never rewritten, enabling and disabling go through links, and disabled vars stay off. The user chose this.

## Decided, don't reopen

- Built-in downloads are Hub only. The user wanted SharpVaMTools mirror links built in. That was declined, because re-uploads may include paid content. The app may read names from a local install and export a list, and the user fetches those by hand.
- Never read or print `D:\X\SharpVaMTools_V3_Release\_Settings.txt`, even filtered. It holds an API key that leaked twice. The user will replace the key after the redesign, so don't remind them.
- Free Hub packages need no login, only the `vamhubconsent=1` cookie VaM sends. On .NET Framework the handler needs `UseCookies = false`, or it drops that header.
- Ask the Hub with `findPackages`. `packages.json` overcounts. Of 347 missing packages, 24 had real links, 290 are in SharpVaMTools data, and 34 are nowhere.

## How to work

- Short plain English in reports. Say what wasn't tested.
- Never run anything against `D:\X` that changes it. Read-only inspection is fine. Use fake folders, as `CLAUDE.md` describes.
- Commit, then run the review loop: 3 subagents in parallel on correctness, regressions and tests, never the Fable model. Fix real findings, commit, repeat until a round is clean, then push. Report findings and rejections.
- Keep the checks green: `dotnet test` in `tests\varManager.Tests` and `tests\varManager.App.Tests`, the MSBuild command in `CLAUDE.md`, and `powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\verify\scripts\smoke.ps1`.
- `.claude\skills\verify\scripts\missing_e2e.ps1` tests Missing end to end on fake folders with the real Hub. It downloads a few small free packages and cancels, and exits non-zero when a check fails. Build Release first.
- The user is on Pro with extra usage off, so keep output short and work inline. Stage exact paths.

## Traps

- Files are CRLF, and many have a BOM. Bash heredocs and `sed` mangle backslashes, so write Python patch scripts with the Write tool, using an assert-once replace. `sed -i` also strips CRLF from the working file.
- In Python, `"..\varManager"` contains a `\v` control character. Escape backslashes.
- `var` is a reserved word in PowerShell, so don't name a function that.
- WinForms timers tick inside modal dialogs. `CanFocus` is false while one is open.
- `backgroundWorkerInstall.RunWorkerAsync` throws when busy, and only `StartBackgroundJob` checks.
- `Directory.GetFiles` is fine for `*.var` with `.part` files, but `File.Exists` is true for broken links.
- UI Automation sees WinForms buttons as panes, so click with `BM_CLICK`.
- Access defaults number columns to 0, so an omitted `filesize` is 0, not NULL.
- `Microsoft.Data.Sqlite` below 10.0.14 pulls in a `SQLitePCLRaw.lib.e_sqlite3` with a known high severity vulnerability (NU1903). Don't downgrade it.
- `git push` sometimes fails with a GitHub 500. Retry it, and confirm with `git status -sb` before reporting a push.
- `LogLevel` lives in the WinForms app, so Core reports through callbacks, such as `Action<string> problem`. The caller wraps those in `BeginInvoke`.
- Access columns hold 255 characters, and adding a longer value throws `ArgumentException`. SQLite has no such limit. In WinForms, catch it per row so one long path can't cost a var its record.
- SharpZipLib decodes zip names without the UTF-8 flag using the system code page, which is UTF-8 on .NET 10 and ANSI on .NET Framework. Non-ASCII names in old vars may differ between the apps. Check this before the new app scans anything, because preview file names come from entry names.
- Smoke's log check ignores the overlong entry's error on purpose, through `Get-LogErrors`.
- Don't chain `git commit` after a smoke run or a `git add` with `;`. Check the result first. A failed `git add` still lets the commit run with whatever was staged.
- `git mv` a file, then stage only its new path. The old path no longer matches, and `git add` fails on it.
- NTFS tunneling can restore a file's creation time when it is deleted and recreated within about 15 seconds, so a test can't use creation time to prove a link wasn't recreated. Use last-write time.
- A failed `VarSwitches.Point` leaves AddonPackages deleted, because it removes the old link first. The old app did too. The new app should show this clearly and offer a retry.
- `SceneAnalysis.CacheFolder` hangs off the working directory, as the old app did. The new app must set a fixed cache folder rather than depend on where it was started. `FormScenes.cs` keeps its own copy of this path, with a `(save).` special case.
- Tests that change the working directory go in the `Working directory` collection, so they never run beside other classes.
- The compiler drops an unused project reference, so `GetReferencedAssemblies` won't list Core until the app calls it.
