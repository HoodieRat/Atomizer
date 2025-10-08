# Atomizer

Atomizer splits large JavaScript files into cohesive modules without breaking existing consumers. It analyzes code with Esprima, plans module groupings, writes grouped ESM/CJS modules, and links them back via bridges and shims so callers keep importing the original path.

## Highlights

- **AST-first analysis** – extracts functions, imports, call graph, identifier usage, and top-level declarations.
- **Safe moveability checks** – only moves functions whose free identifiers are resolvable (built-ins or top-levels).
- **Size-aware planner** – balances module size, respects `MaxFiles`, and merges undersized shards.
- **Callgraph-driven imports** – wires cross-module imports from real call sites (heuristics only if the graph is empty).
- **Bridges & shims** – writes `<file>.js.atomized.js` (ESM) and `<file>.cjs` (CommonJS), replaces the original with a shim, and keeps `_old` backups.
- **Dual runtime output** – grouped modules under `out/<basename>.atomized/*.js` and `out_cjs/<basename>.atomized/*.cjs`.
- **Strong diagnostics** – facts, plans, import previews, export-name maps, smoke checks, and optional HTML span reports.
- **Cleanup ready** – `.gitignore` excludes generated artifacts; `tools/clean_repo.ps1` removes build/run outputs.

## Requirements

- Windows (PowerShell scripts assume Windows paths)
- .NET 8 SDK
- (Optional) Ollama endpoint for LLM-assisted planning

## Quick start

```powershell
# install dependencies and build
dotnet build AtomizeJs.csproj

# configure source (defaults live in data/appstate.json)
notepad data\appstate.json

# run full pipeline (Analyze → Plan → Write → Link → Smoke)
dotnet run --project AtomizeJs.csproj -- run-pipeline

# clean generated artifacts (preview first)
.\tools\clean_repo.ps1 -DryRun
.\tools\clean_repo.ps1
```

## CLI verbs & flags

```
dotnet run --project AtomizeJs.csproj -- [verb] [options]

Verbs:
  analyze        Parse source and emit facts only
  plan           Produce plan.json from existing facts
  write          Emit module files from plan
  link           Insert imports, generate bridges/shims
  run-pipeline   Full sequence (analyze→plan→write→link→smoke)

Common options:
  --source <path>          Override SourceJs
  --outDir <path>          Override OutDir
  --max-files <n>          Cap total modules (including original shard)
  --min-cluster-size <n>   Minimum functions per non-original module
  --dry-run                Report plan/link changes without writing files
```

## Outputs

| Artifact                              | Purpose                                                |
|---------------------------------------|--------------------------------------------------------|
| `facts/`                              | Analyzer facts (`functions.ndjson`, `calls.ndjson`, …) |
| `plans/plan.json`                     | Module assignments                                     |
| `plans/report.json`                   | Summary of moveable vs. kept-in-original               |
| `out/<name>.atomized/`                | ESM modules (including `<name>.js` full shard)         |
| `out_cjs/<name>.atomized/`            | CommonJS modules                                       |
| `<src>.js.atomized.js`                | ESM bridge re-exporting grouped modules                |
| `<src>.cjs`                           | CommonJS bridge                                        |
| `<src>.js`                            | Shim re-exporting the ESM bridge                       |
| `<src>_old*.js`                       | Backups (canonical and timestamped)                    |
| `plans/export-names.json`             | Friendly export name mapping                           |
| `plans/imports.preview.json`          | Import proposals                                       |
| `plans/imports.source.json`           | Callgraph vs heuristic import counts                   |
| `tools/span_report.txt`               | Optional HTML/text span diagnostics                    |

## Tests

```powershell
dotnet test tests\AtomizeJs.Tests.csproj
```

Coverage:
- `AnalyzerTests` – moveability & identifier extraction
- `WriterTests` – slice emission, grouped original shard, explicit exports
- `LinkerTests` – callgraph-driven import insertion

## Configuration knobs (data/appstate.json)

| Field              | Description                                       |
|--------------------|---------------------------------------------------|
| `SourceJs`         | Input JS file                                     |
| `OutDir`           | Output root (ESM)                                 |
| `MaxFiles`         | Total module cap (including original shard)       |
| `MinClusterSize`   | Minimum functions per non-original module         |
| `EnableLLMPlanner` | Use Ollama first, fallback otherwise              |
| `OutputDebug`      | Emit debug copies into `output_debug/`            |

Per-run overrides are available via CLI options.

## Roadmap ideas

- AST-driven writers (pretty-print slices) for perfect formatting
- Dead code/cycle detection
- TS/JSX parsing modes
- HTML visualization of module graph
- Progress reporting & cancellation support for future GUI

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for dev workflow, coding standards, and how to run the pipeline locally. The repo uses MIT License (see [LICENSE](LICENSE)).
