# Redesign handover

Working notes for finishing the new app. Read this first, then `CLAUDE.md`, `docs/kb/roadmap.md` and `docs/kb/mockup/Main.dc.html`. Update this file at the end of every step.

The clickable mockup is at https://claude.ai/artifact/2ugrax4vYy63wpfpRVBKWH. The user approved its direction. Its source is `docs/kb/mockup/Main.dc.html`.

## Where things stand

- Branch `release-0`. `master` is unchanged. Nothing has been merged.
- The real library is migrated: repository `D:\X\VarRepository`, app `D:\X\varManagerApp`, VaM `D:\X\VaM`, with `AddonPackages` a link to the `default` switch.
- The WinForms app has lean profiles, exclude profiles, Hub fixes and the Missing window. It is the app the user runs today, and it must keep working until the new app replaces it.
- Logic with no UI is in `varManager.Core`. The WinForms app links those files.
- `varManager.App` has its shell: theme, top bar, side nav, job bar and a first-run setup panel. Update DB works on `LibraryStore`. The Library page works, including turn on or off and Move out with undo. Other pages are titles only.
- Library measured on a synthetic store with 6,000 vars and 84,000 scene rows, debug build: window up 0.94 s, library shown 1.1 s, search 5 to 55 ms a keystroke, 193 MB. That store has no picture files, so thumbnail cost isn't in those numbers.
- `tests\varManager.App.Tests` runs the Core tests on .NET 10 too. All pass there.
- `LibraryStore` is the new app's database. Add reads to it as screens need them. `savedepens` has no reads or writes yet.
- Import proof on a copy of the real `varManager.mdb`: all 147,943 rows in 5 tables matched, it took 1.3 s, and `LoadVars` read 5,791 vars in 27 ms. The copy was deleted afterwards.

## Resume here

Steps 0 to 11 are done and pushed, and the user has been told it's ready. Step 12 waits on them. `docs/kb/parity.md` lists what still needs their decision.

To try it, the user needs a copy of `varManager.App\bin\Release\net10.0-windows` in a new folder, built with `dotnet build varManager.App\varManager.App.csproj -c Release`, and a settings.json beside it, or the setup panel on first start. With fake folders nothing can go wrong. On the real library, close the old app and VaM first and copy `varManager.mdb`, `varsMigrated.txt` and `varsForInstall.txt` beside the new app, so it imports a copy of the database. `D:\X\varManagerApp` stays as it is until the user approves replacing it.

When the loop reaches step 12 with every earlier step ticked, reviewed clean and pushed: tell the user it's ready, then shut the PC down, as the user asked on 4 November. Not before, and not if anything failed or isn't pushed.

## SDK

The .NET SDK 10.0.403 is installed. If `dotnet --list-sdks` ever shows no 10.x, stop and ask the user to install it. Don't install it yourself.

Visual Studio's MSBuild still can't load SDK-style projects with the SDK installed: `MSB4236: The SDK 'Microsoft.NET.Sdk' specified could not be found`. Keep the links in `varManager.csproj`. Any new Core file needs a link there.

## Steps

Do them in order. Tick each one when it is committed, reviewed and pushed.

- [x] 0. Review commit `558c942` and the handover commit with the loop below, then push.
- [x] 1. Confirm the SDK. Create `varManager.App` (WPF, `net10.0-windows`) referencing `varManager.Core`, with a test project that can target it.
- [x] 2. Add a SQLite store to Core, with a one-time import from `varManager.mdb` that only reads the Access file. Keep the schema simple. Prove it on a copy.
- [x] 3. Move logic out of `Form1.cs` into Core, one area per commit, keeping WinForms working. Moved: Tidy (`VarTidier`, `LibraryFolders`), reading a var for Update DB (`VarScanner`), install and link (`VarLinks`, and `Comm` now lives in Core), switches and the rescan request (`VarSwitches`, `VamBridge`), scenes and previews (`SceneAnalysis`, `ScenePreviews`, load requests in `VamBridge`), dependency analysis (`DependencyAnalysis`). `Form1.cs` went from 4,230 to about 2,880 lines. What stays there is tied to the Access DataSet, or is UI.
- [x] 4. Build the WPF shell from the mockup: dark look, side nav, profile switcher, Update DB, Launch VaM, bottom job bar instead of popups. Update DB is `LibraryUpdate`, on `LibraryStore`. The import runs at startup when `varManager.mdb` sits beside the program and `library.db` doesn't exist yet.
- [x] 5. Library: virtualized gallery that loads only on-screen thumbnails, search with author suggestions and tags, type and status filters, detail panel, turn on or off, Move out with undo.
- [x] 6. Scenes: readiness per profile, one-click Fix, Load in VaM. Also add Load in VaM to the Library's detail panel.
- [x] 7. Profiles: lean and exclude profiles, timing cards, switch, Profile Check. Also add picks to the Library, with the Build profile button in the job bar.
- [x] 8. Health: stale, duplicates, disabled, storage. Nothing fixed without asking.
- [x] 9. Missing: the same one-click flow. Add a "Not owned" Hub filter, image cache, and author search in place of the Creator dropdown. Also watch the repository, so a var dropped in is scanned by itself, as the old app's `WatchRepository` does.
- [x] 10. Parity: list every WinForms button and form, and confirm each has a home in the new app or a stated reason to drop it.
- [x] 11. Prove it on fake folders with a verify script like `smoke.ps1`. Include Fix Rebuild Link and temporary links, which `smoke.ps1` has never covered.
- [ ] 12. Stop. Tell the user it's ready and how to try it. Don't run it on the real library and don't replace `D:\X\varManagerApp` without their approval.

## Design rules from the user

- As few clicks as possible. The app does the checking, and the user only makes real choices.
- Search is one box with suggestions. Authors become removable tags, and tags combine.
- Move out takes a package and dependencies nothing else uses, to a holding folder with undo. It is blocked while other vars need the package. A dependency counts as used if any remaining var, profile, `base.txt` or scene in `Saves` needs it.
- Scanning happens once when a var enters the library, in the background. Never scan at profile switch or launch.
- Only fewer links make VaM start faster. Measured on Megan: full library 46 s startup, 24-28 s load, 10.2 GB. Lean: 24 s, 15-18 s, 4.7 GB.
- Files are never rewritten, enabling and disabling go through links, and disabled vars stay off. The user chose this.

## Decided, don't reopen

- Built-in downloads use the Hub first, then Pixeldrain and Mediafire links from a local SharpVaMTools install, as the user decided on 5 November. Re-uploads may include paid content, so `HubDownloader` drops any mirror var whose `meta.json` says `PC` or `PC EA`. That gate stays. It can't catch a var with a wrong license, and the file is fetched before it can be checked. Mediafire links open a page, so `HubDownloader` reads the file address from it. That page format is from memory, not from a real page. About 132 packages are only on Mediafire. Pixeldrain is fetched anonymously, and the API key in `_Settings.txt` is never read. No real mirror download has been tried yet.
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
- `DependentSaved`, `FixSavseDependencies` and the StaleVars moves still use the Access tables, so the new app needs `savedepens` reads and writes on `LibraryStore`.
- A global `TextBlock` style in WPF overrides a button's `Foreground`, because a button's text is a `TextBlock`. Set text colour on the window instead.
- A `ComboBox` ignores a property change raised while its binding is still setting the value. Disable the box, or post the change. `ShellViewModel.RevertSwitchBox` does the second.
- The new app won't migrate. It refuses a real AddonPackages folder and sends the user to the old app. Step 10 must decide if that stays.
- To see the new app, copy `varManager.App\bin\Debug\net10.0-windows` to a temp folder with a settings.json pointing at fake folders, then take a screenshot with `PrintWindow`. Tests can't catch XAML problems such as bad contrast.
- A script that mutates source to prove a test must not have its output piped through `Select-Object -First`: that stops it before its restore step. Check the file afterwards.
- A mutation that survives means the test is wrong, not that the code is fine. A broken zip header made a whole var invalid, which took a different path from the one the test was meant to reach.
- `RemoveGone` trusts the vars folder. A wrong path with a few vars in it would drop the other records. They rebuild with Update DB, but that took 106 minutes on the real library. Health in step 8 should warn before a large removal.
- For step 12: the new app expects `varManager.mdb`, `varsMigrated.txt` and `varsForInstall.txt` beside itself, in its data folder. Anything the old app left waiting in its own folder needs copying with them.
- In `JobRunner`, `IsRunning` must stay the last thing set, so a waiter sees the finished state.
- WPF has no virtualized wrap panel. The gallery is a virtualized `ListBox` of `TileRow`s, so only rows on screen exist. `Rows` is replaced whole, not changed item by item.
- WPF caches bitmaps by address, even after the file is gone. `ThumbnailLoader` sets `IgnoreImageCache`, or a changed var would show its old picture.
- Popups are separate windows. `PrintWindow` doesn't capture them, and UI Automation finds them under the root, not the main window.
- `LibraryViewModel.Reload` drops any reload that a newer one overtook, so link state can't go stale.
- A background read in `ShellViewModel` can outlive a test. Tests that delete a folder holding `library.db` must retry, as `LibraryUpdateTests.DeleteRoot` does.
- Every finished job reloads the Library, and `Reload` reselects the package. Anything tied to the selection must survive that. A plan was wiped this way, and only pressing the real buttons showed it, because `LibraryViewModel` tests don't go through `ShellViewModel`.
- For anything that changes the library, drive the real window too. `MoveOutTests` now has one test through the shell, and the script is the pattern to follow for step 11.
- Move out re-plans inside its job, because a plan on screen can go stale. A held var keeps its previews, and undo runs Update DB to bring back its records.
- `___HoldingVars___` is skipped by `VarTidier.RepositoryVars`. Any new sorted folder must be added to `SortedFolders`, or Update DB tidies its vars back in.
- `Directory.GetFiles(folder, "A.B.1.*")` also matches the var `A.B.1.2`. Use `MoveOut.FilesOf`'s approach.
- `ListBoxItem` resets text colour, so a `ListBox` row needs `Foreground` set, or its text goes black. Look at a screenshot of every new screen.
- `dotnet test` doesn't build the WinForms app. A Core file with a link in `varManager.csproj` can't use a Core type that has none, such as `LibraryPaths`. Only MSBuild catches that, so run it every time.
- `VarLinks.Installed` ignores case, as the file system does. Before that, a link named in another case looked off, and Load made a duplicate temp link.
- Temp links are removed late, 20 seconds after VaM takes the request. `TempLinkRemoval` keeps one removal waiting, and a newer load replaces it, so an old list can't delete what the newer load needs. A non-scene load has an empty list, so older links wait for the next scene load. A window of milliseconds remains if the old removal fires just before the new load schedules.
- Anything that links a var for good uses `VarLinks.InstallForGood`, so a leftover temp link doesn't show VaM the package twice.
- Checks that pass after a failed patch prove nothing. Look at the patch's own result before reading the test run.
- `Named` finds the first match, so a test package named like a filter pill presses the pill. Give drive packages unusual names, such as `Good.Beach.1`.
- With hardware rendering failing, `PrintWindow` and a screen copy both give a white window. Render with `RenderTargetBitmap` in a small harness that sets software rendering.
- After a rename, `AddonPackages` points at nothing until it is repointed. Do that inside the job, because the reload at job end would otherwise fail and its note would replace the result.
- A finished job reloads before the code after `await job` runs. Tests wait on `ProfilesViewModel.Work`, not a delay.
- Two openers of a new store used to both create the tables. `CreateSchema` now takes the write lock and looks again.
- The app can time startup and RAM, through an empty request the plugin takes. It can't time a scene load, because the plugin doesn't say when one ends. The empty request is untried on a real VaM.
- Anything that reads links outside a job uses `VarLinks.ReadInstalled`, which creates nothing. `Installed` creates `___VarsLink___`, and in the moment `Point` has deleted the link that makes `AddonPackages` a real folder, which fails the switch and blocks every later one. A job-end reload could do this after building a profile. Only running 4 test processes at once showed the cause.
- A test that fails rarely is a bug until shown otherwise. Run it under load to get the real message: 4 `dotnet test` processes at once.
- Health's review can go stale, so every confirmed change checks again: Repair skips anything that isn't still a broken link, delete takes only the reviewed files, and stale vars move only if the list is the same. A record alone isn't a copy, because its file may be the one that's gone.
- `___VarTidied___` with no vars means the folder can't be reached, so nothing counts as gone.
- A `.disabled` file can hold a whole var. Delete only an empty one.
- Records can hold a stale `VarPath`, because Update DB only rescans a changed size. `RemoveGone` matches by name anywhere under `___VarTidied___`, and Health does too.
- Health dropped the mockup's "references only the deep scan found" card: nothing stores that. Old versions other vars still need aren't moved, unlike the old app's option. Say so at step 10.
- A `sed` mutation with `\r$` can quietly not do what it says. If one survives, retry it with an exact replace before believing it.
- Downloads go only to https addresses on virtamate.com: `HubBrowse.IsHubUrl`, applied in `RealHub.Find` and `HubBrowse.Downloads`. Real download links are on hub.virtamate.com. Pictures come from a CDN, so they aren't held to it.
- HttpClient reports a timeout as a cancellation. Only the job's own token means the user cancelled.
- Opening a store creates it, and an empty one stops the Access import. Anything that reads before an Update DB checks the file exists first.
- A store's `LoadVars` is sorted by name, so `A.B.10` comes before `A.B.9`. A test of "newest" needs versions like these.
- The Hub is only asked while Missing is open. The side nav count comes from the database. A real Get from the real Hub worked in the real window on fake folders.
- The stress run clashes in the two `LibraryStoreTests` import tests, because they compare a shared temp folder. Filter them out when running 4 processes.
- `smoke_app.ps1` drives the whole new app in the real window. Run it for anything that changes a flow.
- The window drew white on this PC with hardware rendering, in every build, and correctly in software. This PC has a Virtual Desktop monitor in Error state and two GPUs, but the cause isn't proven. Software is the default, and `HardwareRendering: true` in settings.json turns it back. Speed on 6,000 synthetic vars didn't change. Scrolling wasn't measured.
- A file dialog can move the working directory. `SceneAnalysis.CacheBase` holds the new app's cache in place, so Clear cache can't reach a `Cache` folder somewhere else. Dialogs restore the directory too.
- Settings pause the repository watch, and leaving runs `Open` again, which checks the folders.
- The new app stays without migration: it refuses a real AddonPackages folder, and the old app keeps migration, undo and rollback.
- Before step 12, the new app's data folder needs `varManager.mdb`, `varsMigrated.txt` and `varsForInstall.txt` beside it.
- The compiler drops an unused project reference, so `GetReferencedAssemblies` won't list Core until the app calls it.
