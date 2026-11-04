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

# One test
dotnet test --filter "FullyQualifiedName~Latest_picks_highest"
```

- Tests compile `varManager/VarIndex.cs` through a linked file, so they cover pure logic only. `tests/varManager.Tests` is not in `varManager.sln`.
- `LoadScene.csproj` has hardcoded `F:\VaM` HintPaths. To compile a plugin, call `csc` with `/noconfig /nostdlib+` and reference the DLLs in `<VaM>\VaM_Data\Managed`.
- `MMDLoader` is SDK-style and targets `net6.0-windows`.
- There is no linter and no CI.

## Running the app safely

Never run varManager against a real VaM install to try a change. On startup, `Form1_Load` moves a real `AddonPackages` folder into `___AddonPacksSwitch ___\default` and replaces it with a directory symlink. Update DB then moves vars into the repository.

To smoke test, copy `bin\Release` to a temp folder and set `varspath` and `vampath` in the copied `varManager.exe.config` to fake folders. The fake VaM folder needs a `VaM.exe` file and an `AddonPackages` symlink to `___AddonPacksSwitch ___\default`. The manifest requires admin, so set `__COMPAT_LAYER=RunAsInvoker` to launch without elevation.

Running also needs the Microsoft ACE OLEDB 12.0 provider, x64.

## Architecture

- **`varManager/Form1.cs`** holds most of the logic as well as the UI: tidying, Update DB, install, dependency resolution and VaM integration. Other forms call back into it through a `form1` reference.
- **Data layer:** a typed DataSet (`varManagerDataSet.xsd`, generated `.Designer.cs`) over Access `varManager.mdb`. `vars`, `scenes` and `dependencies` are loaded whole into memory at startup, and `varsView` joins `vars` to `installStatus`. Edit the `.xsd`, not the Designer file.
- **Lookups:** use `VarLookup`, `DependencyLookup` and `ScenesLookup` in `Form1`, not LINQ scans of the tables. They are caches invalidated by DataTable row events. `VarIndex.Resolve` returns a var name, `"missing"`, or a substitute version ending in `$`, and callers depend on that format.
- **Background work:** `StartBackgroundJob("<name>")` runs a named job through `backgroundWorkerInstall`, and `RunBackgroundJob` dispatches on the name. Jobs hold `mutex`. Update the UI with `BeginInvoke`.
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
