using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AtomizeJs.Core;

namespace AtomizeJs.Write
{
    public class ModuleWriter
    {
        private readonly AppState _state;
        private readonly string _outDir;
        private readonly string _groupOutDir;

        public ModuleWriter(AppState state)
        {
            _state = state;
            _outDir = Path.Combine(Directory.GetCurrentDirectory(), _state.OutDir);
            Directory.CreateDirectory(_outDir);
            var baseName = "source";
            try { if (!string.IsNullOrEmpty(_state.SourceJs)) baseName = Path.GetFileNameWithoutExtension(_state.SourceJs) ?? "source"; } catch { }
            var groupDirName = baseName + ".atomized";
            _groupOutDir = Path.Combine(_outDir, groupDirName);
            Directory.CreateDirectory(_groupOutDir);
        }

        // If a slice appears to be unbalanced (unequal braces), try to extend the end to include the matching brace.
        // Returns a (start, end) tuple guaranteed to be within bounds.
        private (int, int) ValidateAndFixSlice(string src, int start, int end)
        {
            if (start < 0) start = 0;
            if (end > src.Length) end = src.Length;
            var snippet = src.Substring(start, Math.Max(0, end - start));
            int open = snippet.Count(c => c == '{');
            int close = snippet.Count(c => c == '}');
            // if balanced or more opens than closes, we'll try expanding end below
            if (open == close) return (start, end);

            // If there are more closing braces than opens, the recorded start may be too late.
            if (close > open)
            {
                int need = close - open;
                int maxBack = Math.Max(0, start - 1024);
                int foundOpen = 0;
                char? strCharBack = null;
                bool inLine = false; bool inBlock = false;
                for (int j = start - 1; j >= maxBack; j--)
                {
                    var cc = src[j];
                    if (inLine)
                    {
                        if (cc == '\n' || cc == '\r') inLine = false; continue;
                    }
                    if (inBlock)
                    {
                        if (cc == '/' && j - 1 >= 0 && src[j - 1] == '*') { inBlock = false; j--; continue; }
                        continue;
                    }
                    if (strCharBack != null)
                    {
                        if (cc == '\\' && j - 1 >= 0) { j--; continue; }
                        if (cc == strCharBack) { strCharBack = null; continue; }
                        if (strCharBack == '`' && cc == '{') { /* ignore */ continue; }
                        continue;
                    }
                    if (cc == '\n' && j - 1 >= 0 && src[j - 1] == '/') { inLine = true; continue; }
                    if (cc == '*' && j - 1 >= 0 && src[j - 1] == '/') { inBlock = true; j--; continue; }
                    if (cc == '"' || cc == '\'' || cc == '`') { strCharBack = cc; continue; }
                    if (cc == '{') { foundOpen++; if (foundOpen >= need) { start = j; break; } }
                }
                // if we moved start, continue to perform forward end expansion below
            }

            // need to find matching closes after end
            int depth = open - close;
            int i = end;
            char? strChar = null;
            int templateExprDepth = 0; // tracks nesting of ${ ... } inside template literals

            for (; i < src.Length; i++)
            {
                var c = src[i];

                // if not inside a string, handle comments and enter strings
                if (strChar == null)
                {
                    if (c == '/' && i + 1 < src.Length)
                    {
                        var n = src[i + 1];
                        if (n == '/') { i += 2; while (i < src.Length && src[i] != '\n') i++; continue; }
                        if (n == '*') { i += 2; while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++; i++; continue; }
                    }
                    if (c == '"' || c == '\'' || c == '`') { strChar = c; continue; }
                    if (c == '{') { depth++; continue; }
                    if (c == '}') { depth--; if (depth == 0) { i++; break; } continue; }
                    continue;
                }

                // inside a string or template literal
                if (c == '\\' && i + 1 < src.Length) { i++; continue; }
                if (strChar == '`')
                {
                    // template literal: ${...} starts a code region where braces count
                    if (c == '$' && i + 1 < src.Length && src[i + 1] == '{')
                    {
                        templateExprDepth++;
                        depth++; // the '{' is part of code
                        i++; // skip the '{' since we've accounted for it
                        continue;
                    }
                    if (c == '`' && templateExprDepth == 0)
                    {
                        strChar = null; continue;
                    }
                    if (c == '}' && templateExprDepth > 0)
                    {
                        templateExprDepth--; depth--; if (depth == 0) { i++; break; } continue;
                    }
                    continue;
                }

                // regular string
                if (c == strChar) { strChar = null; continue; }
            }

            if (depth == 0)
            {
                var newEnd = Math.Min(i, src.Length);
                return (start, newEnd);
            }

            // fallback: find next top-level function-like token to avoid cutting in the middle
            try
            {
                var nextRe = new System.Text.RegularExpressions.Regex("\\r?\\n\\s*(function\\s+|var\\s+|let\\s+|const\\s+|async\\s+function\\s+)", System.Text.RegularExpressions.RegexOptions.Compiled);
                var m = nextRe.Match(src, end);
                if (m.Success)
                {
                    var newEnd = m.Index;
                    return (start, newEnd);
                }
            }
            catch { }

            // last resort: return up to EOF
            return (start, src.Length);
        }

        public void WriteModules()
        {
            var factsDir = AtomizeJs.Utils.EnvPaths.FactsDir;
            var planPath = Path.Combine(AtomizeJs.Utils.EnvPaths.PlansDir, "plan.json");
            if (!File.Exists(planPath)) throw new InvalidOperationException("plan.json not found; run Plan first");
            var planJson = File.ReadAllText(planPath);
            using var planDoc = JsonDocument.Parse(planJson);
            JsonElement modulesEl;
            var root = planDoc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modules", out var m))
            {
                modulesEl = m;
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                modulesEl = root;
            }
            else
            {
                throw new InvalidOperationException("plan.json does not contain a top-level 'modules' array. Preview: " + (planJson.Length > 200 ? planJson.Substring(0, 200) + "..." : planJson));
            }

            if (string.IsNullOrEmpty(_state.SourceJs) || !File.Exists(_state.SourceJs))
                throw new InvalidOperationException("Source JS file is not set or does not exist; set SourceJs and run Analyze first.");

            var sourcePath = Path.GetFullPath(_state.SourceJs);
            var source = File.ReadAllText(sourcePath);

            // read functions shards
            var fpath = Path.Combine(factsDir, "facts.d", "functions.ndjson");
            var funcs = new Dictionary<string, JsonElement>();
            if (File.Exists(fpath))
            {
                foreach (var line in File.ReadLines(fpath))
                {
                    try
                    {
                        var el = JsonDocument.Parse(line).RootElement;
                        funcs[el.GetProperty("id").GetString()!] = el;
                    }
                    catch { }
                }
            }

            int modIndex = 0;
            foreach (var mod in modulesEl.EnumerateArray())
            {
                modIndex++;
                var slug = mod.TryGetProperty("slug", out var slugProp) && slugProp.ValueKind == JsonValueKind.String ? slugProp.GetString() : null;
                var name = mod.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;
                var farr = mod.TryGetProperty("functions", out var functionsProp) ? functionsProp : default;
                if (farr.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine($"Skipping module {slug ?? name ?? ("#"+modIndex)} because it has no 'functions' array.");
                    continue;
                }
                if (farr.GetArrayLength() == 0) continue; // skip empty
                // Default output path is by slug; the 'original' module is named by the actual source basename for clarity.
                var baseNameForOriginal = "source";
                try { if (!string.IsNullOrEmpty(_state.SourceJs)) baseNameForOriginal = Path.GetFileNameWithoutExtension(_state.SourceJs) ?? "source"; } catch { }
                var outFile = Path.Combine(_groupOutDir, slug + ".js");

                // If this is the special 'original' module (contains non-moveable functions),
                // keep the original source intact rather than slicing, to avoid truncation and
                // closure issues. We write the original file into outDir/original.js so bridge
                // and imports can reference it safely.
                if (string.Equals(slug, "original", StringComparison.OrdinalIgnoreCase))
                {
                    // rename the file to the actual source basename for discoverability
                    outFile = Path.Combine(_groupOutDir, baseNameForOriginal + ".js");
                    try
                    {
                        // write banner + original source content into out/original.js
                        using var osw = new StreamWriter(outFile + ".tmp");
                        osw.WriteLine(_state.Banner);
                        osw.WriteLine();
                        osw.Write(source);

                        // append explicit ESM exports for the functions assigned to this module
                        try
                        {
                            var names = new List<string>();
                            if (farr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var fidEl in farr.EnumerateArray())
                                {
                                    var fid = fidEl.ValueKind == JsonValueKind.String ? fidEl.GetString()! : null;
                                    if (string.IsNullOrEmpty(fid) || !funcs.TryGetValue(fid, out var f)) continue;
                                    if (f.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                    {
                                        names.Add(nameEl.GetString()!);
                                    }
                                }
                            }
                            if (names.Count > 0)
                            {
                                osw.WriteLine();
                                osw.WriteLine("// explicit exports added by AtomizeJs for original module");
                                osw.WriteLine("export { " + string.Join(", ", names) + " }; ");
                            }
                        }
                        catch { }

                        osw.Flush(); osw.Close();
                        File.Move(outFile + ".tmp", outFile, true);
                        // remove any prior hardcoded original.js to avoid duplicates
                        try
                        {
                            var hardcoded = Path.Combine(_groupOutDir, "original.js");
                            if (!string.Equals(Path.GetFileName(outFile), "original.js", StringComparison.OrdinalIgnoreCase) && File.Exists(hardcoded))
                            {
                                File.Delete(hardcoded);
                            }
                        }
                        catch { }
                    }
                    catch
                    {
                        // fallback: copy raw source
                        try { File.Copy(sourcePath, outFile, true); } catch { }
                    }
                    // also copy debug
                    if (_state.OutputDebug)
                    {
                        try
                        {
                            var debugDir = Path.Combine(Directory.GetCurrentDirectory(), "output_debug");
                            Directory.CreateDirectory(debugDir);
                            var debugPath = Path.Combine(debugDir, Path.GetFileName(outFile));
                            File.Copy(outFile, debugPath, true);
                        }
                        catch { }
                    }
                    continue;
                }

                using var sw = new StreamWriter(outFile + ".tmp");
                sw.WriteLine(_state.Banner);
                sw.WriteLine();

                // placeholder imports: real linking happens in DependencyRewriter
                sw.WriteLine("// imports will be added by linker");
                sw.WriteLine();

                // write function slices
                foreach (var fidEl in farr.EnumerateArray())
                {
                    var fid = fidEl.ValueKind == JsonValueKind.String ? fidEl.GetString()! : null;
                    if (string.IsNullOrEmpty(fid) || !funcs.TryGetValue(fid, out var f)) continue;
                    if (f.TryGetProperty("span", out var span) && span.ValueKind == JsonValueKind.Object
                        && span.TryGetProperty("start", out var sstart) && span.TryGetProperty("end", out var send))
                    {
                        var start = sstart.GetInt32();
                        var end = send.GetInt32();
                        if (start >= 0 && end > start && end <= source.Length)
                        {
                            // attempt to repair truncated or unbalanced slices by expanding to a balanced brace if needed
                            var (rs, re) = ValidateAndFixSlice(source, start, end);
                            var slice = source.Substring(rs, re - rs);
                            sw.WriteLine(slice);
                            sw.WriteLine();
                        }
                        else
                        {
                            // cannot slice; write a comment placeholder
                            sw.WriteLine($"// [skipped function {fid} — invalid span]");
                        }
                    }
                    else
                    {
                        sw.WriteLine($"// [skipped function {fid} — missing span]");
                    }
                }

                // exports: export functions names
                var exported = new List<string>();
                foreach (var fid in farr.EnumerateArray().Select(x => x.GetString()!))
                {
                    if (!funcs.TryGetValue(fid, out var funcEl)) continue;
                    if (funcEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        exported.Add(nameEl.GetString()!);
                    }
                }
                if (exported.Count > 0)
                {
                    sw.WriteLine();
                    sw.WriteLine("// exports");
                    sw.WriteLine("export { " + string.Join(", ", exported) + " }; ");
                }

                sw.Flush(); sw.Close();
                File.Move(outFile + ".tmp", outFile, true);

                // also write a debug copy inside the workspace so the tool can inspect output without needing access to the user's absolute OutDir
                if (_state.OutputDebug)
                {
                    try
                    {
                        var debugDir = Path.Combine(Directory.GetCurrentDirectory(), "output_debug");
                        Directory.CreateDirectory(debugDir);
                        var debugPath = Path.Combine(debugDir, Path.GetFileName(outFile));
                        File.Copy(outFile, debugPath, true);
                    }
                    catch { }
                }
            }
        }
    }
}
