# Atomizer

Atomizer is a tool for analyzing and splitting large JavaScript files into smaller, manageable modules.

## Features

- **AST-based analysis:** Uses Esprima to extract functions, imports, and call relationships.
- **Moveability detection:** Identifies which functions can be safely moved to new modules.
- **Callgraph-driven imports:** Automatically wires imports between atomized modules.
- **Safe refactoring:** Preserves original files with backups and grouped outputs.
- **Fallbacks:** Handles edge cases with regex-based scanning if AST parsing fails.
- **ESM and CommonJS support:** Outputs modules and bridges for both runtimes.

## Limitations

- Not a transpiler (does not convert JS syntax versions).
- No TypeScript/JSX support.
- No runtime validation or dead code pruning.

## How to use

1. Point Atomizer at your JS file.
2. Run the pipeline (`analyze`, `plan`, `write`, `link`).
3. Use the generated bridge/shim as your new entrypoint.
4. Restore originals anytime from backups.

## Use Cases

- Refactoring legacy or monolithic JS files into modular code.
- Preparing codebases for modernization or migration.
- Auditing code structure and dependencies.

---

*For more details, see the documentation or run the tool with your own JS files.*
