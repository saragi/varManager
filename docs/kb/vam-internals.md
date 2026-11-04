# VaM internals

VaM's source isn't public. This comes from community projects that reimplement or hook its package loader, mainly [VaM_PerformancePlugin](https://github.com/Playable2030/VaM_PerformancePlugin) and [VPB](https://github.com/gicstin/VPB), plus VaM's own log. Treat it as close to VaM's behavior, not guaranteed.

## Package scan

- `FileManager.Refresh` recursively finds `*.var` under `AddonPackages`, then calls `RegisterPackage` for each one, one at a time.
- Each file inside an enabled var is added to several lookup tables, so cost follows total file entries as well as var count.
- When packages change, metadata is reloaded for all packages and refresh handlers run.
- VaM logs `Scanned N packages in X ms` to `%USERPROFILE%\AppData\LocalLow\MeshedVR\VaM\output_log.txt`. The log is replaced on each launch.

## Ways to hide a var

| Method | Still scanned by VaM? |
|---|---|
| Remove the symlink | No |
| Move it out of `AddonPackages` | No |
| Rename to `X.DISABLED` (Qvaro style) | No, it no longer matches `*.var` |
| `X.var.disabled` beside a real `X.var` | Probably yes: VaM builds the package, then checks whether it is enabled |

A `.var.disabled` file can be an empty marker or hold the full var content. VaM treats the var as disabled if the file exists, even when an identical `X.var` sits beside it.

## Dependency references

- Forms: `creator.package.123`, `.latest`, and `.minN`.
- Creator names can contain spaces, for example `s p l i n e  VR`.
- `meta.json` can be wrong or incomplete. An audit of 5,557 vars found 279 unique references in 154 vars that `meta.json` parsing missed, so Update DB also scans scene and preset files. Scene JSON references packages as `creator.package.version:/path`, and `SELF:/` means the same var.
- If an exact version is unavailable or disabled, VaM can fall back to another version. A scene can therefore work in a full library while its requested version is disabled. A lean profile has to include a version that VaM can actually use.

## Other VaM-native files

- `AddonPackagesFilePrefs/<package>/<path>.hide` and `.fav` control visibility and favorites in VaM's browser.
- `AddonPackagesUserPrefs` holds per-package preferences.

Unconfirmed: a `preloadMorphs` option was recalled from memory, and no evidence for it was found in code or searches.
