# Contributing to AtomizeJs

Thanks for your interest in contributing!

## Dev setup
- Requirements: .NET 8 SDK
- Restore and build:
  - `dotnet restore`
  - `dotnet build`

## Running the pipeline
- Configure `data/appstate.json` (created on first run). Set:
  - `SourceJs`: absolute path to the JS file to atomize
  - `OutDir`: output directory (absolute or relative)
  - `MaxFiles`, `MinClusterSize`: sizing knobs
  - `OutputDebug`: optional debug copies into `output_debug/`
- Run end-to-end:
  - `dotnet run --project ./AtomizeJs.csproj --no-build run-pipeline`
- Or step-by-step:
  - `dotnet run --project ./AtomizeJs.csproj --no-build analyze`
  - `dotnet run --project ./AtomizeJs.csproj --no-build plan`
  - `dotnet run --project ./AtomizeJs.csproj --no-build write`
  - `dotnet run --project ./AtomizeJs.csproj --no-build link`

CLI options:
- `--max-files=N` override MaxFiles for this run
- `--min-cluster-size=N` override MinClusterSize for this run

## Tests
- `dotnet test`

## Code style & logging
- Prefer using the simple `Logger` abstraction instead of direct `Console.WriteLine`.

## PRs
- Please include a brief description, and if changing public behavior, mention it in the README.