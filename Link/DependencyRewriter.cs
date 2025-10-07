using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AtomizeJs.Core;
using AtomizeJs.Utils;

namespace AtomizeJs.Link
{
    public class DependencyRewriter
    {
        private readonly AppState _state;

        public DependencyRewriter(AppState state)
        {
            _state = state;
        }

        public void Rewrite()
        {
            var plans = Path.Combine(AtomizeJs.Utils.EnvPaths.PlansDir, "plan.json");
            if (!File.Exists(plans)) throw new InvalidOperationException("plan.json not found; run Plan first");
            var plan = JsonDocument.Parse(File.ReadAllText(plans)).RootElement;

            // Grouped output directory: out/<basename>.atomized
            var outRoot = Path.Combine(Directory.GetCurrentDirectory(), _state.OutDir);
            var baseNameGroup = "source";
            try { if (!string.IsNullOrEmpty(_state.SourceJs)) baseNameGroup = Path.GetFileNameWithoutExtension(_state.SourceJs) ?? "source"; } catch { }
            var outDir = Path.Combine(outRoot, baseNameGroup + ".atomized");
            Directory.CreateDirectory(outDir);
            var modules = plan.GetProperty("modules");
            var moduleMap = new Dictionary<string, string>(StringComparer.Ordinal); // maps function id OR name -> moduleSlug

            // load facts/functions.ndjson to map ids -> names
            var factsIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var fpath = Path.Combine(AtomizeJs.Utils.EnvPaths.FactsDir, "facts.d", "functions.ndjson");
                if (File.Exists(fpath))
                {
                    foreach (var line in File.ReadAllLines(fpath))
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(line);
                            var root = d.RootElement;
                            var id = root.GetProperty("id").GetString();
                            var name = root.GetProperty("name").GetString();
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) factsIdToName[id] = name;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            foreach (var mod in modules.EnumerateArray())
            {
                var slug = mod.GetProperty("slug").GetString()!;
                foreach (var f in mod.GetProperty("functions").EnumerateArray())
                {
                    var fid = f.GetString()!;
                    moduleMap[fid] = slug;
                    if (factsIdToName.TryGetValue(fid, out var nm) && !string.IsNullOrEmpty(nm))
                    {
                        // also map human name -> module
                        if (!moduleMap.ContainsKey(nm)) moduleMap[nm] = slug;
                    }
                }
            }

            // read calls shard
            var callsPath = Path.Combine(AtomizeJs.Utils.EnvPaths.FactsDir, "facts.d", "calls.ndjson");
            var calls = new List<(string caller, string callee)>();
            if (File.Exists(callsPath))
            {
                foreach (var line in File.ReadLines(callsPath))
                {
                    try
                    {
                        var el = JsonDocument.Parse(line).RootElement;
                        calls.Add((el.GetProperty("caller").GetString()!, el.GetProperty("callee").GetString()!));
                    }
                    catch { }
                }
            }

            // For each cross-module call, add import lines to caller file
            var importsByModule = new Dictionary<string, HashSet<string>>();
            bool usedCallgraph = false;
            bool usedHeuristics = false;
            foreach (var (caller, callee) in calls)
            {
                if (!moduleMap.TryGetValue(caller, out var callerMod)) continue;
                if (!moduleMap.TryGetValue(callee, out var calleeMod)) continue;
                if (callerMod == calleeMod) continue;
                if (!importsByModule.ContainsKey(callerMod)) importsByModule[callerMod] = new HashSet<string>();
                importsByModule[callerMod].Add(callee);
            }
            if (calls.Count > 0 && importsByModule.Count > 0) usedCallgraph = true;

            // If callgraph is empty, produce a heuristic preview by scanning module files for symbol usage
            var previewImports = new Dictionary<string, List<string>>();
            if (calls.Count == 0)
            {
                try
                {
                    // build name->module map using facts/functions.ndjson (map actual function NAMES to their module slug)
                    var nameToModule = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        var fpath = Path.Combine(AtomizeJs.Utils.EnvPaths.FactsDir, "facts.d", "functions.ndjson");
                        var idToName = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (File.Exists(fpath))
                        {
                            foreach (var line in File.ReadAllLines(fpath))
                            {
                                try
                                {
                                    using var d = JsonDocument.Parse(line);
                                    var root = d.RootElement;
                                    var id = root.GetProperty("id").GetString();
                                    var name = root.GetProperty("name").GetString();
                                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) idToName[id] = name;
                                }
                                catch { }
                            }
                        }

                        foreach (var mod in plan.GetProperty("modules").EnumerateArray())
                        {
                            var slug = mod.GetProperty("slug").GetString() ?? "";
                            foreach (var fid in mod.GetProperty("functions").EnumerateArray())
                            {
                                var id = fid.GetString();
                                if (id != null && idToName.TryGetValue(id, out var nm) && !string.IsNullOrEmpty(nm))
                                {
                                    nameToModule[nm] = slug;
                                }
                            }
                        }

                        // for each module file, scan text and find occurrence of other-module function names
                        foreach (var mod in plan.GetProperty("modules").EnumerateArray())
                        {
                            var slug = mod.GetProperty("slug").GetString()!;
                            var file = Path.Combine(outDir, slug + ".js");
                            if (!File.Exists(file)) continue;
                            var text = File.ReadAllText(file);
                            var imports = new List<string>();

                            foreach (var kv in nameToModule)
                            {
                                var fn = kv.Key;
                                var fnModule = kv.Value;
                                if (fnModule == slug) continue; // same module
                                try
                                {
                                    // quick word-boundary match
                                    var pattern = "\\b" + System.Text.RegularExpressions.Regex.Escape(fn) + "\\b";
                                    foreach (System.Text.RegularExpressions.Match m2 in System.Text.RegularExpressions.Regex.Matches(text, pattern))
                                    {
                                        // rough heuristic: ensure match is not inside simple quotes/backticks on the same line
                                        var lineStart = text.LastIndexOf('\n', Math.Max(0, m2.Index - 1));
                                        var lineEnd = text.IndexOf('\n', m2.Index);
                                        if (lineEnd < 0) lineEnd = text.Length;
                                        var line = text.Substring(lineStart + 1, lineEnd - lineStart - 1);
                                        // if the match is inside quotes in the line, skip
                                        var before = line.Substring(0, Math.Max(0, m2.Index - (lineStart + 1)));
                                        var after = line.Substring(Math.Max(0, m2.Index - (lineStart + 1)) + m2.Length);
                                        if (before.Count(c => c == '"') % 2 == 1 || before.Count(c => c == '\'') % 2 == 1 || before.Count(c => c == '`') % 2 == 1) continue;
                                        // consider it a candidate
                                        var rel = PathUtil.Rel(Path.GetDirectoryName(file) ?? "", Path.Combine(outDir, fnModule + ".js"));
                                        var lineImport = $"import {{ {fn} }} from '{rel}';";
                                        imports.Add(lineImport);
                                        break; // one occurrence is enough
                                    }
                                }
                                catch { }
                            }

                            if (imports.Count > 0) previewImports[slug] = imports.Distinct().ToList();
                        }
                    }
                    catch { }

                    // write a preview artifact
                    var plansDir = AtomizeJs.Utils.EnvPaths.PlansDir; Directory.CreateDirectory(plansDir);
                    File.WriteAllText(Path.Combine(plansDir, "imports.preview.json"), JsonSerializer.Serialize(previewImports, new JsonSerializerOptions { WriteIndented = true }));

                    // If LLM configured and Ollama provider, attempt a lightweight validation step
                    if (_state.LLM?.Enabled == true && string.Equals(_state.LLM.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var client = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
                            var endpoint = new Uri(new Uri(_state.LLM.Endpoint), "/api/chat");
                            var system = "You are a small code assistant. Given a JS file and a list of candidate import statements, reply with a JSON object {valid:[...], invalid:[...]} naming which imports look necessary based on token usage. Reply only JSON.";
                            var payload = new Dictionary<string, object>
                            {
                                ["model"] = _state.LLM.Model,
                                ["messages"] = new object[] {
                                    new Dictionary<string,object>{ ["role"]="system", ["content"]=system },
                                    new Dictionary<string,object>{ ["role"]="user", ["content"] = new Dictionary<string,object>{ ["preview"] = previewImports } }
                                },
                                ["temperature"] = 0
                            };
                            var content = new System.Net.Http.StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
                            var resp = client.PostAsync(endpoint, content).GetAwaiter().GetResult();
                            var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            var rawPath = Path.Combine(plansDir, "imports.preview.raw.json"); File.WriteAllText(rawPath, txt);
                        }
                        catch { /* ignore LLM validation failures */ }
                    }

                    // print short console summary
                    if (previewImports.Count > 0)
                    {
                        Console.WriteLine("Linker preview: proposed imports detected (calls.ndjson was empty). See plans/imports.preview.json for details.");
                        foreach (var kv in previewImports)
                        {
                            Console.WriteLine($"  {kv.Key}: {kv.Value.Count} imports");
                        }

                        // Convert preview imports into importsByModule entries (extract function name tokens)
                        foreach (var kv in previewImports)
                        {
                            var slug = kv.Key;
                            foreach (var line in kv.Value)
                            {
                                try
                                {
                                    // parse 'import { fn } from 'path';'
                                    var m = System.Text.RegularExpressions.Regex.Match(line, "import\\s*\\{\\s*(?<fn>[A-Za-z_\\$][\\w\\$]*)\\s*\\}");
                                    if (m.Success)
                                    {
                                        var fn = m.Groups["fn"].Value;
                                        if (!importsByModule.ContainsKey(slug)) importsByModule[slug] = new HashSet<string>();
                                        importsByModule[slug].Add(fn);
                                    }
                                }
                                catch { }
                            }
                        }
                        if (importsByModule.Count > 0) usedHeuristics = true;
                    }
                    else
                    {
                        Console.WriteLine("Linker preview: no heuristic imports proposed.");
                    }
                }
                catch { Console.WriteLine("Linker preview step failed"); }
            }

            // Summarize imports source and counts
            try
            {
                var perModuleCounts = importsByModule.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
                var total = perModuleCounts.Values.Sum();
                var src = usedCallgraph ? "callgraph" : (usedHeuristics ? "heuristic" : "none");
                Console.WriteLine($"Linker imports source: {src}; total imports: {total} across {perModuleCounts.Count} modules");
                var plansDir = AtomizeJs.Utils.EnvPaths.PlansDir; Directory.CreateDirectory(plansDir);
                var payload = new Dictionary<string, object>
                {
                    ["source"] = src,
                    ["total"] = total,
                    ["modules"] = perModuleCounts
                };
                File.WriteAllText(Path.Combine(plansDir, "imports.source.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            foreach (var kv in importsByModule)
            {
                // resolve caller file path; 'original' module uses the source basename
                var callerFile = Path.Combine(outDir, kv.Key + ".js");
                if (string.Equals(kv.Key, "original", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var src = _state.SourceJs;
                        if (!string.IsNullOrEmpty(src))
                        {
                            var baseName = Path.GetFileNameWithoutExtension(src);
                            var alt = Path.Combine(outDir, baseName + ".js");
                            if (File.Exists(alt)) callerFile = alt;
                        }
                    }
                    catch { }
                }
                if (!File.Exists(callerFile)) continue;
                var lines = File.ReadAllLines(callerFile).ToList();
                var importLines = new List<string>();
                foreach (var fn in kv.Value)
                {
                    if (!moduleMap.TryGetValue(fn, out var calleeMod)) continue;
                    var calleePath = Path.Combine(outDir, calleeMod + ".js");
                    if (string.Equals(calleeMod, "original", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var src = _state.SourceJs;
                            if (!string.IsNullOrEmpty(src))
                            {
                                var baseName = Path.GetFileNameWithoutExtension(src);
                                var alt = Path.Combine(outDir, baseName + ".js");
                                if (File.Exists(alt)) calleePath = alt;
                            }
                        }
                        catch { }
                    }
                    var rel = PathUtil.Rel(Path.GetDirectoryName(callerFile) ?? "", calleePath);
                    importLines.Add($"import {{ {fn} }} from '{rel}';");
                }
                // insert after banner (assume banner at top)
                var insertAt = 1;
                lines.InsertRange(insertAt, importLines);
                File.WriteAllLines(callerFile + ".tmp", lines);
                File.Move(callerFile + ".tmp", callerFile, true);
            }

            // Generate a bridge/facade at the original source path so callers keep working
            GenerateBridge(plan, outDir);

            // write a simple report about function-to-module mapping for auditing
            try
            {
                var report = new List<Dictionary<string, object>>();
                var funcsAll = new Dictionary<string, string>();
                foreach (var mod in modules.EnumerateArray())
                {
                    var slug = mod.GetProperty("slug").GetString() ?? "";
                    foreach (var f in mod.GetProperty("functions").EnumerateArray())
                    {
                        var fid = f.GetString();
                        if (fid != null) funcsAll[fid] = slug;
                    }
                }

                // enrich with facts/functions.ndjson
                var fpath = Path.Combine(Directory.GetCurrentDirectory(), "facts", "facts.d", "functions.ndjson");
                if (File.Exists(fpath))
                {
                    foreach (var line in File.ReadAllLines(fpath))
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(line);
                            var root = d.RootElement;
                            var id = root.GetProperty("id").GetString();
                            if (id == null) continue;
                            var name = root.GetProperty("name").GetString() ?? "";
                            var span = root.GetProperty("span");
                            bool moveable = true;
                            if (root.TryGetProperty("ext", out var ext) && ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("moveable", out var mv))
                            {
                                if (mv.ValueKind == JsonValueKind.False) moveable = false;
                            }
                            var rec = new Dictionary<string, object>
                            {
                                ["id"] = id,
                                ["name"] = name,
                                ["span"] = new Dictionary<string,int>{ ["start"] = span.GetProperty("start").GetInt32(), ["end"] = span.GetProperty("end").GetInt32() },
                                ["moveable"] = moveable,
                                ["module"] = funcsAll.ContainsKey(id) ? funcsAll[id] : ""
                            };
                            report.Add(rec);
                        }
                        catch { }
                    }
                }
                var plansDir = Path.Combine(Directory.GetCurrentDirectory(), "plans"); Directory.CreateDirectory(plansDir);
                File.WriteAllText(Path.Combine(plansDir, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void GenerateBridge(JsonElement plan, string outDir)
        {
            try
            {
                var src = _state.SourceJs;
                Console.WriteLine($"GenerateBridge: source={src}, outDir={outDir}");
                if (!string.IsNullOrEmpty(src) && File.Exists(src))
                {
                    Console.WriteLine("GenerateBridge: source file exists, proceeding");
                    // Build mapping of function NAME -> module slug using facts and plan
                    var nameToModule = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        var fpath = Path.Combine(AtomizeJs.Utils.EnvPaths.FactsDir, "facts.d", "functions.ndjson");
                        var idToName = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (File.Exists(fpath))
                        {
                            foreach (var line in File.ReadAllLines(fpath))
                            {
                                try
                                {
                                    using var d = JsonDocument.Parse(line);
                                    var root = d.RootElement;
                                    var id = root.GetProperty("id").GetString();
                                    var name = root.GetProperty("name").GetString();
                                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) idToName[id] = name;
                                }
                                catch { }
                            }
                        }

                        var modules = plan.GetProperty("modules");
                        foreach (var mod in modules.EnumerateArray())
                        {
                            var slug = mod.GetProperty("slug").GetString()!;
                            foreach (var fid in mod.GetProperty("functions").EnumerateArray())
                            {
                                var id = fid.GetString();
                                if (id != null && idToName.TryGetValue(id, out var nm) && !string.IsNullOrEmpty(nm))
                                {
                                    // prefer last assignment if duplicates exist; typically unique
                                    nameToModule[nm] = slug;
                                }
                            }
                        }

                        // Build export entries (id->name->module) to compute public names and aliasing if needed
                        var exportEntries = new List<(string Id, string Name, string Module)>();
                        foreach (var kv in nameToModule)
                        {
                            // We need the function id for mapping, recover via idToName if needed
                            // nameToModule uses names as keys, but we can still carry Name and Module
                            exportEntries.Add((Id: kv.Key, Name: kv.Key, Module: kv.Value));
                        }

                        // Helper: check if a name looks like an opaque id (e.g., f0001)
                        bool IsOpaque(string n)
                        {
                            if (string.IsNullOrWhiteSpace(n)) return true;
                            return System.Text.RegularExpressions.Regex.IsMatch(n, "^f\\d{3,}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                        }

                        string ToCamel(string slug)
                        {
                            // from slug like t-placerectangle -> placeRectangle
                            var s = slug;
                            if (s.StartsWith("t-")) s = s.Substring(2);
                            var parts = s.Split(new[]{'-','_','.'}, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 0) return s;
                            var first = parts[0].ToLowerInvariant();
                            var rest = parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1));
                            return first + string.Concat(rest);
                        }

                        // Compute public names, ensuring uniqueness
                        var used = new HashSet<string>(StringComparer.Ordinal);
                        var publicMap = new Dictionary<string, string>(StringComparer.Ordinal); // original name -> public alias (can be same)
                        var moduleCounters = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var e in exportEntries)
                        {
                            var current = e.Name;
                            string pub;
                            if (!IsOpaque(current))
                            {
                                pub = current;
                            }
                            else
                            {
                                var baseName = ToCamel(e.Module);
                                if (!moduleCounters.ContainsKey(e.Module)) moduleCounters[e.Module] = 1;
                                var idx = moduleCounters[e.Module]++;
                                pub = baseName + idx.ToString();
                            }
                            // ensure uniqueness
                            var basePub = pub;
                            int suffix = 2;
                            while (used.Contains(pub)) { pub = basePub + suffix.ToString(); suffix++; }
                            used.Add(pub);
                            publicMap[current] = pub;
                        }

                        // Group by module with public names retained
                        var importsBySlug = new Dictionary<string, List<(string Name, string Public)>>();
                        foreach (var e in exportEntries)
                        {
                            if (!importsBySlug.ContainsKey(e.Module)) importsBySlug[e.Module] = new List<(string, string)>();
                            var pub = publicMap[e.Name];
                            importsBySlug[e.Module].Add((e.Name, pub));
                        }

                        // build bridge lines
                        var bridgeLines = new List<string>();
                        bridgeLines.Add("'use strict';");
                        bridgeLines.Add("");

                        foreach (var kv in importsBySlug)
                        {
                            var slug = kv.Key;
                            var fnames = kv.Value;
                            // 'original' module file is named after the source basename in grouped dir
                            string targetFile;
                            if (string.Equals(slug, "original", StringComparison.OrdinalIgnoreCase))
                            {
                                var baseName = Path.GetFileNameWithoutExtension(src);
                                targetFile = Path.Combine(outDir, baseName + ".js");
                            }
                            else
                            {
                                targetFile = Path.Combine(outDir, slug + ".js");
                            }
                            var rel = PathUtil.Rel(Path.GetDirectoryName(src) ?? "", targetFile);
                            // Use aliasing if public name differs
                            var parts = new List<string>();
                            foreach (var (name, pub) in fnames)
                            {
                                if (!string.Equals(name, pub, StringComparison.Ordinal)) parts.Add($"{name} as {pub}");
                                else parts.Add(name);
                            }
                            var importLine = "import { " + string.Join(", ", parts) + " } from '" + rel + "';";
                            bridgeLines.Add(importLine);
                        }

                        bridgeLines.Add("");
                        // preserve original top-level lines until first export or module.exports
                        var orig = File.ReadAllLines(src);
                        for (int i = 0; i < orig.Length; i++)
                        {
                            var line = orig[i];
                            if (line.TrimStart().StartsWith("export ") || line.Contains("module.exports") || line.Contains("exports.")) break;
                            bridgeLines.Add(line);
                        }

                        bridgeLines.Add("");
                        // re-export names in a stable order
                        // export public names in stable order
                        var exported = publicMap.Values.OrderBy(n => n, StringComparer.Ordinal).ToList();
                        if (exported.Count > 0)
                        {
                            bridgeLines.Add("// re-exports generated by AtomizeJs");
                            bridgeLines.Add("export { " + string.Join(", ", exported) + " }; ");
                        }

                        // persist a mapping artifact for auditing (original name -> public alias)
                        try
                        {
                            var plansDir2 = AtomizeJs.Utils.EnvPaths.PlansDir; Directory.CreateDirectory(plansDir2);
                            var mapObj = publicMap.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                            File.WriteAllText(Path.Combine(plansDir2, "export-names.json"), JsonSerializer.Serialize(mapObj, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        catch { }

                        // Generate CommonJS variants under out_cjs and a CJS bridge at sourcePath.cjs
                        try
                        {
                            // CommonJS grouped output directory: out_cjs/<basename>.atomized
                            var cjsRoot = Path.Combine(Directory.GetCurrentDirectory(), _state.OutDir + "_cjs");
                            var baseNameGroupLocal = "source";
                            try { if (!string.IsNullOrEmpty(src)) baseNameGroupLocal = Path.GetFileNameWithoutExtension(src) ?? "source"; } catch { }
                            var cjsOutDir = Path.Combine(cjsRoot, baseNameGroupLocal + ".atomized");
                            Directory.CreateDirectory(cjsOutDir);

                            // Transform each out module to CJS
                            foreach (var mod in modules.EnumerateArray())
                            {
                                var slug = mod.GetProperty("slug").GetString() ?? string.Empty;
                                if (string.IsNullOrEmpty(slug)) continue;
                                string esmPath;
                                if (string.Equals(slug, "original", StringComparison.OrdinalIgnoreCase))
                                {
                                    var baseName = Path.GetFileNameWithoutExtension(src);
                                    esmPath = Path.Combine(outDir, baseName + ".js");
                                }
                                else
                                {
                                    esmPath = Path.Combine(outDir, slug + ".js");
                                }
                                if (!File.Exists(esmPath)) continue;
                                var txt = File.ReadAllText(esmPath);
                                // 1) import -> const require
                                try
                                {
                                    var reImport = new System.Text.RegularExpressions.Regex("^\\s*import\\s*\\{\\s*([^}]*)\\}\\s*from\\s*['\"]([^'\"]+)['\"];?", System.Text.RegularExpressions.RegexOptions.Multiline);
                                    txt = reImport.Replace(txt, m =>
                                    {
                                        var names = m.Groups[1].Value.Trim();
                                        var from = m.Groups[2].Value.Trim();
                                        // adjust extension to .cjs for relative module imports
                                        var adjusted = from;
                                        try
                                        {
                                            if (from.StartsWith(".") && from.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                                            {
                                                adjusted = from.Substring(0, from.Length - 3) + ".cjs";
                                            }
                                        }
                                        catch { }
                                        return $"const {{ {names} }} = require('{adjusted}');";
                                    });
                                }
                                catch { }

                                // 2) collect export names and remove export lines
                                var exportNames = new List<string>();
                                try
                                {
                                    var reExport = new System.Text.RegularExpressions.Regex("^\\s*export\\s*\\{\\s*([^}]*)\\s*\\}\\s*;?\\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
                                    txt = reExport.Replace(txt, m =>
                                    {
                                        var inner = m.Groups[1].Value;
                                        var parts = inner.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                        foreach (var p in parts)
                                        {
                                            var t = p.Trim();
                                            var asIdx = t.IndexOf(" as ", StringComparison.Ordinal);
                                            if (asIdx >= 0) t = t.Substring(0, asIdx).Trim();
                                            if (!string.IsNullOrEmpty(t)) exportNames.Add(t);
                                        }
                                        return string.Empty;
                                    });
                                }
                                catch { }

                                // 3) append module.exports
                                if (exportNames.Count > 0)
                                {
                                    txt += "\nmodule.exports = { " + string.Join(", ", exportNames.Distinct()) + " };\n";
                                }

                                // Name the CJS file using baseName for 'original'
                                var cjsName = string.Equals(slug, "original", StringComparison.OrdinalIgnoreCase)
                                    ? (Path.GetFileNameWithoutExtension(src) + ".cjs")
                                    : (slug + ".cjs");
                                var cjsPath = Path.Combine(cjsOutDir, cjsName);
                                File.WriteAllText(cjsPath, txt);
                            }

                            // Create CJS bridge at sourcePath.cjs that re-exports public names
                            try
                            {
                                var srcDir = Path.GetDirectoryName(src) ?? string.Empty;
                                var baseName = Path.GetFileNameWithoutExtension(src);
                                var cjsBridge = Path.Combine(srcDir, baseName + ".cjs");
                                var lines = new List<string>();
                                lines.Add("'use strict';");
                                lines.Add("");

                                // require each module with original names
                                foreach (var kv in importsBySlug)
                                {
                                    var slug = kv.Key;
                                    var pairs = kv.Value; // (original, public)
                                    var cjsFile = string.Equals(slug, "original", StringComparison.OrdinalIgnoreCase)
                                        ? (Path.GetFileNameWithoutExtension(src) + ".cjs")
                                        : (slug + ".cjs");
                                    var rel = AtomizeJs.Utils.PathUtil.Rel(srcDir, Path.Combine(cjsOutDir, cjsFile));
                                    var origNames = string.Join(", ", pairs.Select(p => p.Name).Distinct());
                                    lines.Add($"const {{ {origNames} }} = require('{rel}');");
                                }

                                lines.Add("");
                                // module.exports = { public: original, ... }
                                var mappings = new List<string>();
                                foreach (var kv in importsBySlug)
                                {
                                    foreach (var (name, pub) in kv.Value)
                                    {
                                        // unique mapping entries only
                                        var entry = pub + ": " + name;
                                        mappings.Add(entry);
                                    }
                                }
                                // ensure deterministic order and dedupe
                                var mapDistinct = mappings.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
                                lines.Add("module.exports = { " + string.Join(", ", mapDistinct) + " };");

                                File.WriteAllLines(cjsBridge + ".tmp", lines);
                                File.Move(cjsBridge + ".tmp", cjsBridge, true);
                                Console.WriteLine($"Wrote CommonJS bridge: {cjsBridge}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to write CJS bridge: {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to generate CommonJS variants: {ex.Message}");
                        }

                        // write the atomized bridge next to the source (src + .atomized.js)
                        var atomizedPath = src + ".atomized.js";
                        try
                        {
                            var tmpAtom = atomizedPath + ".tmp";
                            File.WriteAllLines(tmpAtom, bridgeLines);
                            // copy to final atomized path (overwrite if exists), then remove tmp
                            File.Copy(tmpAtom, atomizedPath, true);
                            File.Delete(tmpAtom);
                            Console.WriteLine($"Wrote atomized bridge: {atomizedPath}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to write atomized bridge to {atomizedPath}: {ex.Message}");
                        }

                        // Backup original by appending _old before extension (e.g., t_old.js)
                        try
                        {
                            var srcDir = Path.GetDirectoryName(src) ?? "";
                            var baseName = Path.GetFileNameWithoutExtension(src);
                            var ext = Path.GetExtension(src);
                            var backupName = Path.Combine(srcDir, baseName + "_old" + ext);
                            // if backup exists, create a timestamped variant
                            if (File.Exists(backupName))
                            {
                                var tstamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                                backupName = Path.Combine(srcDir, baseName + "_old_" + tstamp + ext);
                            }
                            try
                            {
                                Console.WriteLine($"Attempting to back up original {src} -> {backupName}");
                                File.Copy(src, backupName, false);
                                Console.WriteLine($"Backed up original to: {backupName}");
                            }
                            catch (Exception ex)
                            {
                                var tstamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                                var backupName2 = Path.Combine(srcDir, baseName + "_old_" + tstamp + ext);
                                try { Console.WriteLine($"Attempting timestamped backup {backupName2}"); File.Copy(src, backupName2, false); Console.WriteLine($"Backed up original to: {backupName2}"); }
                                catch (Exception ex2) { Console.WriteLine($"Failed to back up original {src}: {ex.Message}; fallback error: {ex2.Message}"); }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to back up original {src}: {ex.Message}");
                        }

                        // Create a small shim at the original path that re-exports the atomized bridge
                        try
                        {
                            var shimLines = new List<string>();
                            shimLines.Add("'use strict';");
                            shimLines.Add("");
                            shimLines.Add("// Shim re-exporting atomized bridge generated by AtomizeJs");
                            var atomRel = Path.GetFileName(atomizedPath);
                            shimLines.Add($"export * from './{atomRel}';");
                            var tmpShim = src + ".shim.tmp";
                            File.WriteAllLines(tmpShim, shimLines);
                            File.Move(tmpShim, src, true);
                            Console.WriteLine($"Wrote shim at original path to re-export atomized bridge: {src}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to write shim at original path {src}: {ex.Message}");
                        }

                        // Also copy a backup of the original into the out directory as t_old.js for visibility
                        try
                        {
                            var srcDir = Path.GetDirectoryName(src) ?? "";
                            var baseName = Path.GetFileNameWithoutExtension(src);
                            var ext = Path.GetExtension(src);
                            var canonicalOld = Path.Combine(srcDir, baseName + "_old" + ext);
                            string? bestBackup = null;
                            if (File.Exists(canonicalOld)) bestBackup = canonicalOld;
                            else
                            {
                                var pattern = baseName + "_old_*.js";
                                var options = Directory.GetFiles(srcDir, pattern).OrderByDescending(f => new FileInfo(f).Length).ToList();
                                if (options.Count > 0) bestBackup = options[0];
                            }
                            if (!string.IsNullOrEmpty(bestBackup) && File.Exists(bestBackup))
                            {
                                Directory.CreateDirectory(outDir);
                                var outOld = Path.Combine(outDir, baseName + "_old" + ext);
                                File.Copy(bestBackup, outOld, true);
                                Console.WriteLine($"Copied backup to out: {outOld}");
                            }
                            else
                            {
                                Console.WriteLine("No suitable backup found to copy into out directory.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to copy backup into out directory: {ex.Message}");
                        }
                    }
                    catch
                    {
                        // best-effort: if anything fails, write a minimal bridge that re-exports existing atomized file
                        try
                        {
                            var tmp2 = src + ".bridge.tmp";
                            var fallback = new List<string> {
                                "'use strict';",
                                "",
                                "// Fallback bridge generated by AtomizeJs",
                                $"export * from '{Path.GetFileName(src)}.atomized.js';"
                            };
                            File.WriteAllLines(tmp2, fallback);
                            File.Move(tmp2, src + ".atomized.js", true);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}

