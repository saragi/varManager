# Parity with the WinForms app

Every button and form in the old app, and where it went in the new one. The old app stays in the repository and keeps working, so nothing here is lost when the new app is tried.

## Has a home

| Old app | New app |
|---|---|
| UPD_DB | Update DB in the top bar. It also runs by itself when a var is dropped in the repository |
| Settings | Settings in the top bar |
| Launch VaM | Launch VaM in the top bar, which also times the launch |
| Profile box, Add, Rename, Del | Profile box in the top bar. Profiles page: Make an empty profile, Rename, Delete |
| Lean Profile, Exclude Profile | Profiles page, built from picks made in the Library |
| Profile Check, Repair, Restore previous | Check on a profile's card |
| Install Selected, preview Install | Turn on in the Library, with dependencies |
| UnInst Selected, `FormUninstallVars` | Turn off in the Library, which takes what depends on it too |
| Delete Selected | Move out in the Library, with undo. Deleting for good is a reviewed step in Health |
| Export Insted | Profiles page: Save a list of linked vars |
| Install By TXT | Profiles page: Turn on from a list |
| Stale Vars, `FormStaleVars` | Health: Old versions nothing uses, moved with undo |
| Fix Preview | Health: Preview pictures missing |
| Rebuild symlink | Health: Broken links in this profile. It repairs broken links only, because a working link needs nothing |
| Installed Packages, All Packages, `FormMissing` | Missing page: This profile, Whole library |
| "Saves" JsonFile | Missing page: Your saves |
| Brow, `FormHub`, `HubItem` | Missing page: Browse the Hub |
| Scan Hub for updates | Missing page: Updates on the Hub |
| Load, Merge | Load in VaM on the Scenes page and in the Library, with Merge on the Scenes page |
| Locate | Show in Library on a Hub tile. The Library's search finds a var by name |
| Clear Cache | Health: Scene cache. It clears every scene, where the old button cleared one |
| Reset, filters | Search box, author tags and pills in the Library |
| `FormVarDetail` | The Library's detail panel: dependencies and what needs the package. Saves that use it appear when Move out plans |
| `FormSettings` | The setup panel, from Settings |
| `FormSwitchAdd`, `FormSwitchRename` | Profiles page |
| `FormLeanProfile`, `FormExcludeProfile`, `FormProfileCheck` | Profiles page |

## Dropped, with the reason

| Old app | Reason |
|---|---|
| Move SeleLinks To SubDir, `FormVarsMove` | It sorted links into subfolders by hand. Profiles replace that, and linked vars in subfolders still count as linked |
| Stale Vars' old-version option | It moved versions other vars still need and repointed links. Health only moves what nothing uses, so a scene can't break |
| Filtered Packages, Selected vars in `FormMissing` | The new app has no multi-select in the Library. This profile and Whole library cover the same need, and a lean profile makes a smaller scope |
| Google button in `FormMissingVars` | Built-in help for finding a package stays on the Hub |
| `FormMigrationPreview`, `--undo-migration`, `--rollback-migration` | The library is already migrated. The new app refuses a real AddonPackages and sends the user to the old app, which keeps these |
| Mockup's "references only the deep scan found" card | Nothing stores that count, and there's nothing to act on |
| `Form2` | An empty form |

## Not in the new app yet

These are real features with no home. They need a decision from the user, and the old app still has them.

| Old app | What it does | Why it isn't built |
|---|---|---|
| Hide and Fav, `FormScenes` | Marks scenes hidden or favourite, and browses looks, clothing, hair, poses, skin and morphs by category | 1,168 lines. The new Scenes page lists scenes only |
| Loading looks, poses and other non-scene items, with For Male, Ignore gender and Person Order | Applies an item to a chosen person in the open scene | The Library loads a package's first scene. These options only mean something for these items |
| Library preview strip | Pages through every item inside a package | The Library shows one picture per package |
| Analysis, `FormAnalysis` | Lists a scene's atoms and people, and loads a look from a scene | 1,091 lines, and it needs the plugin's analysis mode |
| Manual substitutes, `FormMissingVars` | Links a missing name to another var through `___MissingVarLink___`, and saves the list | Missing lists what's lacking but can't point it at something else |
| Prepare Form Saves, `PrepareSaves` | Picks saves by tree and works out the vars they need | Your saves covers all saves at once, but not a chosen few |
| Log analysis | Reads missing packages from VaM's log | Missing works from the database and your saves instead |
| Scene load time on a profile card | Needs the plugin to say when a load ends | A change to the plugin, in both copies |
