using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AtomizeJs.Core;

namespace AtomizeJs.Health
{
    public class Smoke
    {
        private readonly AppState _state;
        public Smoke(AppState state) { _state = state; }

        public bool RunChecks()
        {
            var ok = true;
            var planPath = Path.Combine(Directory.GetCurrentDirectory(), "plans", "plan.json");
            if (!File.Exists(planPath))
            {
                Console.WriteLine("plan.json missing"); return false;
            }
            var plan = JsonDocument.Parse(File.ReadAllText(planPath)).RootElement;
            var modules = plan.GetProperty("modules");
            var count = modules.GetArrayLength();
            if (count > _state.MaxFiles)
            {
                Console.WriteLine($"Module count {count} > MaxFiles {_state.MaxFiles}"); ok = false;
            }

            foreach (var mod in modules.EnumerateArray())
            {
                var slug = mod.GetProperty("slug").GetString();
                var fcount = mod.GetProperty("functions").GetArrayLength();
                if (fcount == 0)
                {
                    Console.WriteLine($"Module {slug} is empty"); ok = false; continue;
                }
                if (fcount < _state.MinClusterSize)
                {
                    Console.WriteLine($"Module {slug} smaller than MinClusterSize ({fcount} < {_state.MinClusterSize})");
                }
            }

            // build checks
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), _state.OutDir);
            if (!Directory.Exists(outDir)) { Console.WriteLine("Out dir missing"); return false; }
            var files = Directory.GetFiles(outDir, "*.js");
            foreach (var f in files)
            {
                var txt = File.ReadAllText(f);
                if (string.IsNullOrWhiteSpace(txt)) { Console.WriteLine($"Empty file: {f}"); ok = false; }
            }

            // Facade checks: ensure shim and bridge export expected names
            try
            {
                var src = _state.SourceJs;
                if (!string.IsNullOrEmpty(src))
                {
                    // 1) Shim should re-export atomized bridge
                    if (File.Exists(src))
                    {
                        var shim = File.ReadAllText(src);
                        if (!(shim.Contains("export * from") && shim.Contains(".atomized.js")))
                        {
                            Console.WriteLine("Smoke: Source file does not look like a shim re-exporting the atomized bridge.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Smoke: SourceJs not found: {src}"); ok = false;
                    }

                    // 2) Atomized bridge must exist and export all expected public names
                    var atom = src + ".atomized.js";
                    if (!File.Exists(atom))
                    {
                        Console.WriteLine($"Smoke: Atomized bridge not found: {atom}"); ok = false;
                    }
                    else
                    {
                        var atomTxt = File.ReadAllText(atom);
                        var exported = ExtractExportedNames(atomTxt);
                        if (exported.Count == 0)
                        {
                            Console.WriteLine("Smoke: No exported names found in atomized bridge."); ok = false;
                        }

                        // If we have a mapping, verify that all public names appear in exports
                        var mapPath = Path.Combine(Directory.GetCurrentDirectory(), "plans", "export-names.json");
                        if (File.Exists(mapPath))
                        {
                            try
                            {
                                var mapJson = File.ReadAllText(mapPath);
                                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(mapJson) ?? new Dictionary<string,string>();
                                var expected = new HashSet<string>(dict.Values, StringComparer.Ordinal);
                                foreach (var name in expected)
                                {
                                    if (!exported.Contains(name))
                                    {
                                        Console.WriteLine($"Smoke: Expected export '{name}' not found in atomized bridge."); ok = false;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    // 3) Ensure the original module file (named after source basename) contains explicit exports to surface its functions
                    var baseName = Path.GetFileNameWithoutExtension(src) ?? "original";
                    var originalPath = Path.Combine(outDir, baseName + ".js");
                    if (File.Exists(originalPath))
                    {
                        var origTxt = File.ReadAllText(originalPath);
                        if (!origTxt.Contains("export {"))
                        {
                            Console.WriteLine("Smoke: out/original.js does not contain explicit exports block.");
                        }
                    }
                }
            }
            catch { }

            return ok;
        }

        // naive exported names extractor: finds all occurrences of `export { a, b, c }` and collects the names
        private static HashSet<string> ExtractExportedNames(string code)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var re = new System.Text.RegularExpressions.Regex(@"export\s*\{([^}]*)\}", System.Text.RegularExpressions.RegexOptions.Multiline);
                foreach (System.Text.RegularExpressions.Match m in re.Matches(code))
                {
                    var inner = m.Groups[1].Value;
                    var parts = inner.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        var t = p.Trim();
                        // handle alias `name as pub`
                        var asIdx = t.IndexOf(" as ", StringComparison.Ordinal);
                        if (asIdx >= 0) t = t.Substring(asIdx + 4);
                        t = t.Trim();
                        if (!string.IsNullOrEmpty(t)) set.Add(t);
                    }
                }
            }
            catch { }
            return set;
        }
    }
}
