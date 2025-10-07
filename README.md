# AtomizeJs — Usage Guide

This project atomizes a large JS file into deterministic modules and wires a safe facade so callers don’t need to change. It produces both ESM and CommonJS outputs.

## What gets generated
- <source>.js — stable entrypoint (shim). Re-exports the atomized bridge.
- <source>.js.atomized.js — ESM bridge that imports from `out/<basename>.atomized/*.js` and re-exports friendly names.
- <source>.cjs — CommonJS bridge that requires from `out_cjs/<basename>.atomized/*.cjs` and `module.exports = { ... }`.
- out/<basename>.atomized/*.js — ESM atomized modules (including `<basename>.js` for the preserved original with explicit exports).
- out_cjs/<basename>.atomized/*.cjs — CommonJS equivalents of the atomized modules.
- <source>_old.js and <source>_old_*.js — Original file backups (canonical and timestamped).
- out/<basename>.atomized/<basename>_old.js — Convenience copy of the original.
- plans/export-names.json — Mapping of original names → public export names used by the bridge.

## How to use in your project
- ESM (import/export):
  - Keep importing the same entrypoint you used before (the original filename). Example:
    ```js
    import { t_moveTo, renderHeader } from './path/to/t.js';
    ```
- CommonJS (require/module.exports):
  - Require the CJS bridge:
    ```js
    const api = require('./path/to/t.cjs');
    // e.g., api.t_moveTo(...), api.renderHeader(...)
    ```

No code changes to existing call sites are required if they continue pointing to the original filename.

## Run the full pipeline
- Non-interactive end-to-end run:
  ```powershell
  dotnet run --project .\AtomizeJs.csproj --no-build run-pipeline
  ```

Example with the bundled sample:
1) Edit `data/appstate.json` and set `SourceJs` to the absolute path of `input/sample/sample.js`.
2) Run the command above. See outputs under `out/sample.atomized/` and bridges next to the sample.
- The pipeline does: Analyze → Plan → Write → Link → Smoke.
  - If `t.js` is a shim, the analyzer auto-restores a suitable backup before analyzing to avoid empty outputs.

## Restore the original file
- Quick restore (PowerShell helper):
  ```powershell
  .\tools\restore_original.ps1 -Target "input/t.js"
  ```
  The script prefers non-shim backups and falls back to the largest backup or `out/t_old.js`.
- Manual restore:
  ```powershell
  Copy-Item -Path .\input\t_old.js -Destination .\input\t.js -Force
  ```

## Smoke checks
The pipeline runs smoke checks that verify:
- The shim re-exports the atomized bridge.
- The bridge exports the expected public names (from `plans/export-names.json`).
- `out/original.js` contains explicit exports for its functions.

## Notes
- ESM and CJS outputs are generated together. Use whichever your project needs.
- Naming: the bridge exposes friendly names (no `f0001`-style IDs). See `plans/export-names.json`.
- Backups: originals are preserved next to the source with both canonical and timestamped filenames; a copy is placed in `out/<basename>.atomized/<basename>_old.js`.
- Optional debug copies: set `OutputDebug: true` in `data/appstate.json` to mirror the written modules into `output_debug/` for quick inspection.

## Troubleshooting
- Analyzer found 0 functions / `out` is empty:
  - Likely analyzed a shim. Re-run the pipeline; it auto-restores a backup, or run the restore script above.
- Need direct module imports:
  - You can import individual modules from `out/<basename>.atomized/*.js` (ESM) or require from `out_cjs/<basename>.atomized/*.cjs` (CJS), but the facade (`<source>.js` / `<source>.cjs`) is recommended to keep call sites stable.

## Publishing to GitHub
- A `.gitignore` excludes build artifacts, outputs, analysis facts, local appstate, and generated backups/bridges.
- Add a license (MIT included).
- CI (optional): set up a .NET build workflow to validate PRs.
