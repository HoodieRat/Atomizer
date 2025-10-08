
# Atomizer

A .NET 8 CLI that atomizes large JavaScript files into deterministic modules, wires safe facades (ESM & CommonJS), and keeps the original entrypoint working. Atomizer parses code with Esprima, plans modules via Ollama (optional) or deterministic heuristics, writes grouped modules, links imports, and runs smoke checks to keep refactors safe.

---

## ✨ Why it’s useful

- **Zero-touch refactors** – Existing imports keep pointing at the original file; Atomizer drops in a shim and bridges to the new modules.
- **Deterministic analysis** – Uses AST parsing to find functions, imports, callgraphs, top-level identifiers, and moveability.
- **Balanced planning** – Optional Ollama plan for “human” groupings; deterministic fallback respects size, `MaxFiles`, and `MinClusterSize`.
- **Cross-runtime output** – Generates grouped ESM modules, CommonJS counterparts, and bridges for both.
- **Audit trails** – Facts, plans, import previews, export-name maps, HTML reports, and smoke checks.
- **Safety nets** – Backups, shim detection, automatic restore, and `tools/restore_original.ps1`.

---

Cleanup helper:

```powershell
# preview deletions
.\tools\clean_repo.ps1 -DryRun

# remove generated artifacts (out/, facts/, plans/, bin/, obj/, etc.)
.\tools\clean_repo.ps1
```

> The provided script is `tools/clean_repo.ps1` in this repo.

```powershell
dotnet run --project .\AtomizeJs.csproj -- run-pipeline
```

---

## ⚙️ Features in detail

### Analyzer (deterministic)
- Esprima-based AST walk; falls back to regex only if parsing fails.
- Captures spans, exports, identifier usage, free variables, top-level declarations.
- Marks functions moveable only if their free identifiers are safe (built-ins or top-level).
- Writes facts to `facts/` with environment overrides via `Utils/EnvPaths`.

### Planner (Ollama-first, heuristic fallback)
- If `AppState.LLM.Enabled` and provider is `Ollama`, posts `facts.index.json` to `/api/chat` for module suggestions.
- Persists raw responses to `plans/plan.raw.json` for auditing.
- Rejects unexpected responses and falls back to `HeuristicAssist`.
- `HeuristicAssist` bins functions by span size, respects `MaxFiles`, enforces `MinClusterSize`, and keeps non-moveables in an `original` shard.

### Writer
- Groups outputs under `out/<basename>.atomized/` and writes the preserved original as `<basename>.js` with explicit exports.
- Slices functions safely, patching unbalanced braces when needed.
- Honors `_state.Banner` and optional debug mirroring to `output_debug/`.

### Linker
- Uses callgraph facts to insert cross-module imports; if empty, runs a heuristic text scan and (optionally) asks Ollama to validate.
- Generates ESM bridge `<source>.js.atomized.js`, CommonJS bridge `<source>.cjs`, and grouped CJS modules under `out_cjs/<basename>.atomized/`.
- Replaces the original file with a shim re-exporting the ESM bridge, writes backups (`_old*.js`), and copies backups into `out/`.
- Produces `plans/export-names.json`, `plans/imports.preview.json`, and `plans/imports.source.json` for auditing.

### Smoke checks
- Verifies module counts against `MaxFiles`/`MinClusterSize`.
- Ensures ESM/CJS bridges and shims export expected names.
- Confirms the original shard contains explicit exports.

---

## 🛠️ CLI verbs & options

```powershell
dotnet run --project .\AtomizeJs.csproj -- [verb] [options]

Verbs:
  analyze        # Parse source, emit facts only
  plan           # Produce plan.json (Ollama → fallback)
  write          # Emit grouped modules
  link           # Wire imports, generate bridges/shims/backups
  run-pipeline   # Full sequence analyze→plan→write→link→smoke

Options (set via CLI or data/appstate.json):
  --max-files=<n>          # Cap total modules (including original shard)
  --min-cluster-size=<n>   # Minimum functions per non-original module
  --dry-run                # (future flag) reserved

Environment overrides:
  ATOMIZER_FACTS_DIR       # Alternate facts directory
  ATOMIZER_PLANS_DIR       # Alternate plans directory
  ATOMIZER_AUTO=1          # Non-interactive full run on startup
```

Interactive mode presents the same steps via a menu in `Program.cs`.

---

## 📂 Key outputs

| Path / File                                        | Purpose |
|----------------------------------------------------|---------|
| `out/<basename>.atomized/<module>.js`              | ESM modules; `<basename>.js` keeps the original shard |
| `out_cjs/<basename>.atomized/<module>.cjs`         | CommonJS equivalents |
| `<source>.js.atomized.js`                          | ESM bridge re-exporting grouped modules |
| `<source>.cjs`                                     | CommonJS bridge mirroring the ESM exports |
| `<source>.js`                                      | Shim re-exporting the ESM bridge |
| `<source>_old*.js`                                 | Canonical + timestamped backups |
| `facts/facts.d/*.ndjson`, `facts.index.json`       | Analyzer facts |
| `plans/plan.json`, `plan.raw.json`, `report.json`  | Planner output & summaries |
| `plans/export-names.json`                          | Original name → public alias map |
| `plans/imports.preview.json` / `imports.source.json`| Heuristic import suggestions & provenance |
| `output/report.html`                               | Optional HTML module size report |

Cleanup helper:

```powershell
# preview deletions
./tools/clean_repo.ps1 -DryRun

# remove generated artifacts (out/, facts/, plans/, bin/, obj/, etc.)
./tools/clean_repo.ps1
```

> The provided script is `tools/clean_repo.ps1` in this repo.

---

## 🧪 Tests

```powershell
dotnet test .\tests\AtomizeJs.Tests.csproj
```

Coverage:
- `AnalyzerTests` – verifies moveability detection and facts emission.
- `WriterTests` – ensures the preserved original shard gets explicit exports.
- `LinkerTests` – forces cross-module calls and checks imports are inserted.

Parallelism is disabled to avoid shared temp directories clashing.

---

## 🔧 Configuration (data/appstate.json)

| Setting         | Default | Notes |
|-----------------|---------|-------|
| `SourceJs`      | `null`  | Absolute path to the JS file to atomize |
| `OutDir`        | `"out"`| Root for grouped ESM modules |
| `MaxFiles`      | `12`    | Includes the original shard |
| `MinClusterSize`| `3`     | Minimum functions per non-original module |
| `OutputDebug`   | `false` | Mirror outputs into `output_debug/` |
| `LLM.*`         | see file | Enables Ollama planning & preview validation |

You can override facts/plans directories via environment variables or set `MaxFiles`/`MinClusterSize` from the CLI.

---

## 🤖 LLM integration (optional)

- **Planner:** `OllamaPlanner` posts facts to an Ollama endpoint. A valid JSON `{ modules: [...] }` response replaces the deterministic plan. Anything else falls back to `HeuristicAssist`.
- **Linker preview:** When callgraph data is absent and heuristics propose imports, the linker can ask Ollama to validate `imports.preview.json`. Failures are ignored—heuristics still apply.
- **Opt-out:** Set `AppState.LLM.Enabled = false` or remove Ollama configuration; the pipeline remains fully deterministic.

---

## 🚀 Development & contributing

- Restore/build: `dotnet build AtomizeJs.csproj`
- Run pipeline: `dotnet run --project AtomizeJs.csproj -- run-pipeline`
- Tests: `dotnet test tests/AtomizeJs.Tests.csproj`
- Clean: `tools/clean_repo.ps1`
- Restore originals: `tools/restore_original.ps1 -Target <path>`

See [CONTRIBUTING.md](CONTRIBUTING.md) for workflow details, coding standards, and guidelines.

---

## 📄 License

MIT — see [LICENSE](LICENSE).

---

Happy atomizing! Let me know if you build new planners, writers, or GUI shells on top of this.
