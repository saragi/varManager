# Performance findings

## VaM with fewer vars

Test PC: NVMe SSD, 32 GB RAM, RTX 5070 Ti. Scene: `PetaZwega.Megan.3`, loaded with the normal scene browser after restarting VaM. These are warm loads, because Windows still had the files cached. Startup times are estimates from CPU and responsiveness.

| | Full library (5,696 packages) | Lean links (22–23 packages) |
|---|---|---|
| Package scan | 7.9 s | 0.07 s |
| Startup | About 46 s | About 24 s |
| Scene load | 24–28 s | About 15 s |
| RAM after startup | About 7.9 GB | About 2.1 GB |
| RAM with scene loaded | About 10.2 GB | About 4.7 GB |
| Clothing and hair refresh | 2.33 s | 0.05 s |
| Loaded objects | About 513,000 | About 108,000 |

- The first start after adding thousands of vars had log activity for about 271 s. BrowserAssist rewrote its index in that run but not the next, so it was probably one-time indexing. This isn't proven.
- A start after a reboot has not been measured.
- Var management won't fix in-scene FPS, which depends on physics and scene settings.

### Lesson from the test

The first lean list loaded with no obvious failure, but the scene's options didn't work. It had missed a var whose creator name contains spaces, and it kept two plugin versions disabled when the full library relied on newer versions. A profile needs a check of plugin and hair references, not just a clean load.

## App hot spots

Synthetic benchmark on .NET Framework 4.8: 15,000 vars, 90,000 dependency rows, 120,000 scene rows, 3,000 installed vars.

| Pattern | Before | After |
|---|---|---|
| Dependency scan per installed var | About 12.5 s | About 15 ms with a dictionary |
| 16 regexes over a 15-entry cache, 20,000 zip entries | About 760 ms | About 90 ms with a larger cache |
| `List.Contains` var reconciliation | About 400 ms | Under 1 ms with `HashSet` |
| Scene scan per selected var | About 6 ms | Near zero with a lookup |

Release 0 fixed these, plus the per-file `savedepens` scan in the saved file dependency check.

## Still open

From a read-only code review, not measured. Add timing before changing them.

1. `UpdateVarsInstalled` clears and rewrites install status row by row. It runs after operations, not inside per-var loops.
2. `UpdateVarsViewDataGridView` refills all of `varsView` and restores the selection.
3. `FillDataTables` loads tables one after another. A parallel version is commented out with no recorded reason, so don't simply re-enable it.
4. `listViewPreviewPics_RetrieveVirtualItem` decodes full-size images on the UI thread. Saved thumbnails would likely fix this. `backgroundWorkerPreview_DoWork` and its 5 ms sleep are commented-out code, so they cost nothing.
5. Update DB writes and sweeps whole tables for each new var, about 7 ms per var at 120,000 scene rows, and scans vars one at a time. This is mostly a one-time cost, so leave it unless measurements show a real problem.

`UpdDB` already skips vars whose name is in the database. It doesn't detect a changed file with the same name.

Access via OLEDB sits behind items 1–3, and SQLite is planned for Release 4, so avoid large investments in Access-specific code. Don't rewrite in Rust or C++: the costs are design and data access, and in-game plugins must stay C# because VaM compiles them.
