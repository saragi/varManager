# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

varManager manages Virt-A-Mate (VaM) `.var` packages. Vars live in a separate repository folder, and VaM sees only what is symlinked into `AddonPackages`. Background and measurements are in [docs/kb](docs/kb/README.md).

## Commands

The main app is a legacy-format .NET Framework 4.8 project. Build it with Visual Studio's MSBuild, not `dotnet build`. The targeting pack is not needed because `Microsoft.NETFramework.ReferenceAssemblies` supplies it.

```powershell
# Build
& 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe' varManager\varManager.csproj /restore /t:Build /p:Configuration=Release /p:Platform=AnyCPU /v:m

# All tests (run from tests\varManager.Tests)
dotnet test

# New WPF app
dotnet build varManager.App\varManager.App.csproj

# Core and app tests on .NET 10 (run from tests\varManager.App.Tests)
dotnet test

# One test
dotnet test --filter "FullyQualifiedName~Latest_picks_highest"
```

- Logic with no UI lives in `varManager.Core`. Tests reference that project, so they cover that logic only. The WinForms app compiles the same files through links, because Visual Studio's MSBuild here has no .NET SDK to load a modern project. A new file the WinForms app uses needs a link in `varManager.csproj`. `tests/varManager.Tests` is not in `varManager.sln`.
- Build `LoadScene.csproj` with `/restore /p:RestorePackagesConfig=true /p:SolutionDir=<repo>\ /p:VaMPath=<VaM folder>`. It references VaM's DLLs through `VaMPath`.
- `MMDLoader` is SDK-style and targets `net6.0-windows`.
- `varManager.App` is the new WPF app on `net10.0-windows`, with namespace `varManager.Ui`. Build it with `dotnet`, because Visual Studio's MSBuild still can't load SDK-style projects. It isn't in `varManager.sln`. `tests/varManager.App.Tests` compiles the Core tests from `tests/varManager.Tests` as well as its own.
- There is no linter and no CI.

## Running the app safely

Never run varManager against a real VaM install to try a change. If `AddonPackages` is a real folder, startup shows a migration preview. Confirming it moves vars into the repository, then the rest of the folder moves into `___AddonPacksSwitch ___\default` and is replaced with a directory symlink.

To smoke test, copy `bin\Release` to a temp folder and set `varspath` and `vampath` in the copied `varManager.exe.config` to fake folders. The fake VaM folder needs a `VaM.exe` file and an `AddonPackages` folder. The manifest requires admin, so set `__COMPAT_LAYER=RunAsInvoker` to launch without elevation. UI Automation sees this app's WinForms buttons as panes with no Invoke pattern, so click them by posting `BM_CLICK` to their window handle.

Running also needs the Microsoft ACE OLEDB 12.0 provider, x64.

## Architecture

- **`varManager/Form1.cs`** holds the UI, the background jobs and everything tied to the Access DataSet, such as Update DB's loop and writes. Other forms call back into it through a `form1` reference.
- **`varManager.Core`** holds the logic with no UI, and both apps use it: `VarTidier`, `VarScanner`, `VarLinks`, `VarSwitches`, `VamBridge`, `SceneAnalysis`, `ScenePreviews`, `DependencyAnalysis` and the older classes named below. It reports through callbacks, because `LogLevel` belongs to the WinForms app.
- **Data layer:** a typed DataSet (`varManagerDataSet.xsd`, generated `.Designer.cs`) over Access `varManager.mdb`. `vars`, `scenes` and `dependencies` are loaded whole into memory at startup, and `varsView` joins `vars` to `installStatus`. Edit the `.xsd`, not the Designer file.
- **New app's data:** `LibraryStore` is a SQLite database with the same tables and column names as Access, and var names ignore case. `AccessDatabase.ImportOnce` fills it once from a temporary copy of `varManager.mdb`, so the original is never opened. `LibraryUpdate` is the new app's Update DB. Only the new app uses these, so they have no link in `varManager.csproj`.
- **Lookups:** use `VarLookup`, `DependencyLookup` and `ScenesLookup` in `Form1`, not LINQ scans of the tables. They are caches invalidated by DataTable row events. `VarIndex.Resolve` returns a var name, `"missing"`, or a substitute version ending in `$`, and callers depend on that format.
- **Dependencies:** `DependencyScanner` reads `meta.json` and scene or preset files. Update DB scans new vars, and rescans a known var only when its file size changes.
- **Lean profiles:** a profile is a switch whose `___VarsLink___` holds only the links it needs. `LeanProfiles` plans it from the selected vars plus a base set: session plugins, the default scene and `<varspath>\___Profiles___\base.txt`. Each profile saves its direct picks in `___Profiles___\<name>.profile`, with the prior version as `.previous`. An exclude profile instead saves exclusions: it links what the default switch links, minus them and dependencies only they use, and never follows references to vars default doesn't link, so disabled vars stay off.
- **Missing:** `MissingPlanner` works out what the library needs from the database, `FormMissing` asks the Hub, and `HubDownloader` stores free packages in the repository root as `.part` files until complete. Update DB then scans them, and `varsMigrated.txt` links exactly what linked vars need. Vars dropped in the repository root are scanned automatically. `SharpVaMToolsData` reads package names only, from an install beside the VaM folder, and never its `_Settings.txt`.
- **Migration:** `LibraryMigration.Plan` only reads, `Apply` logs each move before making it, and `Undo` replays the log (`varManager.exe --undo-migration <log>`). `Rollback` also removes links and restores a real `AddonPackages` (`--rollback-migration <log>`). Enabled vars go into `varsMigrated.txt`, which Update DB links back exactly, without following dependencies. Disabled content is stored as plain vars and left unlinked.
- **Background work:** `StartBackgroundJob("<name>")` runs a named job through `backgroundWorkerInstall`, and `RunBackgroundJob` dispatches on the name. Jobs hold `mutex`. Update the UI with `BeginInvoke`.
- **Move out:** `MoveOut.Plan` works out what moves and what stays. `Apply` logs to `moved.txt` before moving files to `<varspath>\___HoldingVars___\<time>\`, and `Undo` replays it.
- **Folder layout:** vars are tidied into `<varspath>\___VarTidied___\<creator>\`, with sibling `___*___` folders for preview images, redundant, stale, old-version and deleted vars. Inside `AddonPackages`, links go in `___VarsLink___`, `___TempVarLink___` and `___MissingVarLink___`.
- **Switches:** `AddonPackages` is a directory symlink to `<vam>\___AddonPacksSwitch ___\<name>`. Changing switch repoints that link.
- **VaM integration:** the app writes `Custom\PluginData\feelfar\loadscene.json`, and a plugin running inside VaM polls for it and deletes it. It handles rescans and scene or preset loading.
- **Two plugin copies:** `varManager/Custom/Scripts/loadscene.cs` is the older one, copied to the app output. `LoadScene/src` is newer and adds MMD support. A change to the protocol must go in both. Plugin code runs on VaM's Unity main thread and must not block it.
- **`ThirdParty/DgvFilterPopup`** is vendored CPOL source retargeted to .NET Framework 4.8, because the NuGet package only supports modern .NET.
- **`SimpleJSON`** is duplicated in `varManager` and `MMDLoader`.

## Conventions

- Source files use CRLF, and many use a UTF-8 BOM. Keep both.
- Var names are `creator.package.version`. Creator and package names can contain spaces, and references can end in `.latest` or `.minN`.
- The app must never rewrite the contents of a var.
- Built-in downloads stay on the VaM Hub.
