# varManager verification map

This map lists what the `verify` skill can drive. It holds one journey by choice, the first-run smoke test. Read [../SKILL.md](../SKILL.md) for doctor, launch and cleanup.

## Baseline preconditions

- Doctor passes.
- Every run uses a fresh sandbox under `%TEMP%\varManager-verify\run\<stamp>`.
- Never drive an instance this run did not start, and never use a real VaM install.

## Features

- [First-run smoke test](first-run-smoke.md): migration preview, cancel, confirm on seed data, Update DB.

## Not covered

No journey exists for these, so report them as unverified rather than covered by the smoke test:

- Installing, uninstalling and removing vars from the grid
- Dependency analysis and the missing-vars window
- Scenes, presets, the Hub browser, stale and old-version handling
- Switch add, rename and change
- `--undo-migration`
- The plugin protocol with a running VaM (`loadscene.json`), and `LoadScene` or `MMDLoader`
- Real library scale, real var content, and a cross-drive vars path
- Elevated launch through the app manifest
