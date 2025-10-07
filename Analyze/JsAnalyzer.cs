using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtomizeJs.Core;
using Esprima;
using Esprima.Ast;

namespace AtomizeJs.Analyze
{
    public class JsAnalyzer
    {
        private readonly AppState _state;
        private readonly string _factsDir;

        public JsAnalyzer(AppState state)
        {
            _state = state;
            _factsDir = AtomizeJs.Utils.EnvPaths.FactsDir;
            Directory.CreateDirectory(_factsDir);
            Directory.CreateDirectory(Path.Combine(_factsDir, "facts.d"));
        }

        public void Analyze()
        {
            var sourcePath = _state.SourceJs ?? throw new InvalidOperationException("SourceJs not set");
            var source = File.ReadAllText(sourcePath);

            var functions = new List<Dictionary<string, object>>();
            var imports = new List<Dictionary<string, object>>();
            var calls = new List<Dictionary<string, object>>();

            int fid = 1;

            string AddFunctionRecord(string name, int startIdx, int endIdx, bool exported, bool moveable)
            {
                var id = $"f{fid:D4}"; fid++;
                functions.Add(new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["id"] = id,
                    ["name"] = name,
                    ["exported"] = exported,
                    ["span"] = new Dictionary<string,int>{ ["start"] = startIdx, ["end"] = endIdx },
                    ["ext"] = new Dictionary<string, object>{ ["identifiers.body"] = new List<string>(), ["moveable"] = moveable }
                });
                return id;
            }

            int FindJSDocStart(string src, int idx)
            {
                var look = Math.Max(0, idx - 300);
                var pos = src.LastIndexOf("/**", idx);
                if (pos >= look) return pos;
                return -1;
            }

            bool IsExported(string src, int idx)
            {
                var lookStart = Math.Max(0, idx - 80);
                var snippet = src.Substring(lookStart, Math.Min(80, idx - lookStart));
                return Regex.IsMatch(snippet, "\\bexport\\b");
            }

            // Try AST-based extraction first (more precise). Fall back to text scanner on error.
            // Keep a place to record top-level declared names for downstream linking/exports
            var topLevelNames = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                var parser = new JavaScriptParser(source, new ParserOptions { Tolerant = true });
                var program = parser.ParseScript();

                // helper: collect calls under a given function body using reflective traversal
                void CollectCalls(Node node, string callerId)
                {
                    if (node == null) return;
                    // Avoid traversing into nested function definitions; they'll be analyzed separately when encountered at top-level
                    if (node is FunctionDeclaration || node is FunctionExpression || node is ArrowFunctionExpression) return;

                    if (node is CallExpression call)
                    {
                        string? calleeName = null;
                        try
                        {
                            if (call.Callee is Identifier id1)
                            {
                                calleeName = id1.Name;
                            }
                            else if (call.Callee is MemberExpression me && me.Property is Identifier pid && me.Computed == false)
                            {
                                calleeName = pid.Name;
                            }
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(calleeName))
                        {
                            calls.Add(new Dictionary<string, object>
                            {
                                ["type"] = "call",
                                ["caller"] = callerId,
                                ["callee"] = calleeName!
                            });
                        }
                    }

                    // Reflect children
                    try
                    {
                        foreach (var prop in node.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        {
                            object? val = null;
                            try { val = prop.GetValue(node); } catch { }
                            if (val == null) continue;
                            if (val is Node child)
                            {
                                CollectCalls(child, callerId);
                            }
                            else if (val is IEnumerable en)
                            {
                                foreach (var item in en)
                                {
                                    if (item is Node cn) CollectCalls(cn, callerId);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // helper: validate/adjust end index to get balanced braces inside slice
                int ValidateEnd(string src, int start, int end)
                {
                    // safety limits
                    int maxTry = Math.Min(src.Length, end + 4096);

                    // If the initial slice has more closing braces than opens, try moving the start back a bit
                    int initialLen = Math.Max(0, end - start);
                    if (initialLen > 0 && start >= 0 && end <= src.Length)
                    {
                        var initSlice = src.Substring(start, initialLen);
                        int initOpen = initSlice.Count(c => c == '{');
                        int initClose = initSlice.Count(c => c == '}');
                        if (initClose > initOpen)
                        {
                            int need = initClose - initOpen;
                            int maxBack = Math.Max(0, start - 1024);
                            int found = 0;
                            for (int j = start - 1; j >= maxBack; j--)
                            {
                                if (src[j] == '{') { found++; if (found >= need) { start = j; break; } }
                            }
                        }
                    }

                    for (int e = end; e <= maxTry; e++)
                    {
                        var len = Math.Max(0, e - start);
                        if (start + len > src.Length) break;
                        var slice = src.Substring(start, len);

                        // First attempt: quick local scan for balanced braces/strings/comments (template-aware)
                        int depth = 0; char? strChar = null; bool inLine = false; bool inBlock = false;
                        bool ok = true;
                        for (int i = 0; i < slice.Length; i++)
                        {
                            var c = slice[i];
                            if (inLine)
                            {
                                if (c == '\n' || c == '\r') inLine = false; continue;
                            }
                            if (inBlock)
                            {
                                if (c == '*' && i + 1 < slice.Length && slice[i + 1] == '/') { inBlock = false; i++; continue; }
                                continue;
                            }
                            if (strChar != null)
                            {
                                if (c == '\\' && i + 1 < slice.Length) { i++; continue; }
                                if (c == strChar) { strChar = null; continue; }
                                if (strChar == '`' && c == '$' && i + 1 < slice.Length && slice[i + 1] == '{') { depth++; i++; continue; }
                                if (strChar == '`' && c == '}' && depth > 0) { depth--; continue; }
                                continue;
                            }
                            if (c == '/' && i + 1 < slice.Length)
                            {
                                var n = slice[i + 1];
                                if (n == '/') { inLine = true; i++; continue; }
                                if (n == '*') { inBlock = true; i++; continue; }
                            }
                            if (c == '"' || c == '\'' || c == '`') { strChar = c; continue; }
                            if (c == '{') { depth++; continue; }
                            if (c == '}') { depth--; if (depth < 0) { ok = false; break; } continue; }
                        }

                        if (strChar == null && !inLine && !inBlock && depth == 0 && ok)
                        {
                            // quick scan succeeded
                            return e;
                        }

                        // Second attempt: try parsing the candidate slice with Esprima (tolerant parse)
                        // This allows accepting slices that the quick scanner might not like but are syntactically valid
                        try
                        {
                            var parser = new JavaScriptParser(slice, new ParserOptions { Tolerant = true });
                            var program = parser.ParseScript();
                            // If parse completed without throwing, accept this end
                            return e;
                        }
                        catch
                        {
                            // parse failed — continue expanding
                        }
                    }
                    return end;
                }

                // collect top-level declared names to detect closures/free-vars
                foreach (var top in program.Body)
                {
                    try
                    {
                        if (top is FunctionDeclaration tfd && tfd.Id != null && tfd.Id.Name != null) topLevelNames.Add(tfd.Id.Name);
                        else if (top is VariableDeclaration tvd)
                        {
                            foreach (var d in tvd.Declarations)
                            {
                                if (d is VariableDeclarator vdec)
                                {
                                    if (vdec.Id is Identifier iid && iid.Name != null) topLevelNames.Add(iid.Name);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Builtins that should not prevent moveability
                var builtins = new HashSet<string>(new[] {
                    "Math","Date","JSON","console","Object","Array","Number","String","Boolean","Promise","Set","Map","WeakMap","WeakSet","Symbol","Reflect","BigInt","RegExp","Error","Intl","URL","URLSearchParams","TextEncoder","TextDecoder","AbortController","fetch","Headers","Request","Response",
                    // Node globals
                    "Buffer","require","module","exports","process","__dirname","__filename","global","globalThis"
                }, StringComparer.Ordinal);

                // Helper to collect identifiers usage within a function body and compute free identifiers
                (HashSet<string> used, HashSet<string> declaredLocals) CollectIdents(Node body, IEnumerable<string> paramNames)
                {
                    var used = new HashSet<string>(StringComparer.Ordinal);
                    var declared = new HashSet<string>(paramNames, StringComparer.Ordinal);

                    void Walk(Node node, Node? parent)
                    {
                        if (node == null) return;
                        // Do not traverse into nested function definitions to avoid capturing inner scopes
                        if (node is FunctionDeclaration || node is FunctionExpression || node is ArrowFunctionExpression) return;

                        switch (node)
                        {
                            case VariableDeclarator vd when vd.Id is Identifier id:
                                if (!string.IsNullOrEmpty(id.Name)) declared.Add(id.Name);
                                break;
                            case FunctionDeclaration fdecl when fdecl.Id is Identifier fid && !string.IsNullOrEmpty(fid.Name):
                                declared.Add(fid.Name);
                                break;
                            case CatchClause cc:
                                try { if (cc.Param is Identifier cid && !string.IsNullOrEmpty(cid.Name)) declared.Add(cid.Name); } catch {}
                                break;
                        }

                        // Record identifier usages in expression/statement contexts
                        if (node is Identifier ident)
                        {
                            // Skip if part of a property key or non-computed member expression property
                            bool isPropertyKey = false;
                            try
                            {
                                if (parent is MemberExpression me && object.ReferenceEquals(me.Property, node) && me.Computed == false) isPropertyKey = true;
                                else if (parent is Property p && object.ReferenceEquals(p.Key, node) && p.Computed == false) isPropertyKey = true;
                                else if (parent is MethodDefinition) isPropertyKey = true;
                            }
                            catch { }
                            if (!isPropertyKey && !string.IsNullOrEmpty(ident.Name)) used.Add(ident.Name);
                        }

                        // Recurse
                        try
                        {
                            foreach (var prop in node.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                            {
                                object? val = null;
                                try { val = prop.GetValue(node); } catch { }
                                if (val == null) continue;
                                if (val is Node child) Walk(child, node);
                                else if (val is IEnumerable en)
                                {
                                    foreach (var item in en)
                                    {
                                        if (item is Node cn) Walk(cn, node);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    Walk(body, null);
                    return (used, declared);
                }

                foreach (var node in program.Body)
                {
                    try
                    {
                        // function declarations
                        if (node is FunctionDeclaration fd)
                        {
                            var name = fd.Id?.Name ?? "<anonymous>";
                            var span = fd.Range; var spanStart = span.Start;
                            // prefer body range end when available for accurate closing brace
                            var spanEnd = fd.Body != null ? fd.Body.Range.End : span.End;
                            var jsdoc = FindJSDocStart(source, spanStart);
                            var recordStart = jsdoc >= 0 ? jsdoc : spanStart;
                            var adjEnd = ValidateEnd(source, recordStart, spanEnd);
                            // collect identifiers and compute free vars
                            var paramNames = new List<string>();
                            try { foreach (var p in fd.Params) if (p is Identifier pid && !string.IsNullOrEmpty(pid.Name)) paramNames.Add(pid.Name); } catch {}
                            var (used, declared) = fd.Body != null ? CollectIdents(fd.Body, paramNames) : (new HashSet<string>(), new HashSet<string>());
                            var free = new HashSet<string>(used, StringComparer.Ordinal);
                            foreach (var d in declared) free.Remove(d);
                            // allow references to builtins and top-level names
                            free.RemoveWhere(n => builtins.Contains(n) || topLevelNames.Contains(n) || n == name);
                            bool moveable = free.Count == 0;
                            var callerId = AddFunctionRecord(name, recordStart, adjEnd, IsExported(source, spanStart), moveable);
                            // attach ext identifiers to the last function we added
                            try
                            {
                                var rec = functions.Last();
                                if (rec.TryGetValue("ext", out var extObj) && extObj is Dictionary<string, object> extDict)
                                {
                                    extDict["identifiers.body"] = used.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["identifiers.free"] = (used.Count == 0) ? new List<string>() : free.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["declared.params"] = paramNames.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["declared.locals"] = declared.Except(paramNames, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["moveable"] = moveable;
                                }
                            }
                            catch { }
                            try { if (fd.Body != null) CollectCalls(fd.Body, callerId); } catch { }
                            continue;
                        }

                        // export declarations wrapping functions
                        if (node is ExportNamedDeclaration en && en.Declaration is FunctionDeclaration efd)
                        {
                            var name = efd.Id?.Name ?? "<anonymous>";
                            var span = efd.Range; var spanStart = span.Start;
                            var spanEnd = efd.Body != null ? efd.Body.Range.End : span.End;
                            var jsdoc = FindJSDocStart(source, spanStart);
                            var recordStart = jsdoc >= 0 ? jsdoc : spanStart;
                            var adjEnd = ValidateEnd(source, recordStart, spanEnd);
                            // collect identifiers and compute free vars
                            var paramNames = new List<string>();
                            try { foreach (var p in efd.Params) if (p is Identifier pid && !string.IsNullOrEmpty(pid.Name)) paramNames.Add(pid.Name); } catch {}
                            var (used, declared) = efd.Body != null ? CollectIdents(efd.Body, paramNames) : (new HashSet<string>(), new HashSet<string>());
                            var free = new HashSet<string>(used, StringComparer.Ordinal);
                            foreach (var d in declared) free.Remove(d);
                            free.RemoveWhere(n => builtins.Contains(n) || topLevelNames.Contains(n) || n == name);
                            bool moveable = free.Count == 0;
                            var callerId = AddFunctionRecord(name, recordStart, adjEnd, true, moveable);
                            try
                            {
                                var rec = functions.Last();
                                if (rec.TryGetValue("ext", out var extObj) && extObj is Dictionary<string, object> extDict)
                                {
                                    extDict["identifiers.body"] = used.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["identifiers.free"] = (used.Count == 0) ? new List<string>() : free.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["declared.params"] = paramNames.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["declared.locals"] = declared.Except(paramNames, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["moveable"] = moveable;
                                }
                            }
                            catch { }
                            try { if (efd.Body != null) CollectCalls(efd.Body, callerId); } catch { }
                            continue;
                        }

                        // variable declarations with function/arrow initializers
                        if (node is VariableDeclaration vd)
                        {
                            foreach (var decl in vd.Declarations)
                            {
                                if (decl is VariableDeclarator vdec)
                                {
                                    var idName = (vdec.Id as Identifier)?.Name ?? "<anon>";
                                    var init = vdec.Init;
                                    if (init is FunctionExpression fe)
                                    {
                                        var span = fe.Range; var spanStart = span.Start;
                                        var spanEnd = fe.Body != null ? fe.Body.Range.End : span.End;
                                        var jsdoc = FindJSDocStart(source, spanStart);
                                        var recordStart = jsdoc >= 0 ? jsdoc : (int)vdec.Range.Start;
                                        var adjEnd = ValidateEnd(source, recordStart, spanEnd);
                                        // collect identifiers and compute free vars
                                        var paramNames = new List<string>();
                                        try { foreach (var p in fe.Params) if (p is Identifier pid && !string.IsNullOrEmpty(pid.Name)) paramNames.Add(pid.Name); } catch {}
                                        var (used, declared) = fe.Body != null ? CollectIdents(fe.Body, paramNames) : (new HashSet<string>(), new HashSet<string>());
                                        var free = new HashSet<string>(used, StringComparer.Ordinal);
                                        foreach (var d in declared) free.Remove(d);
                                        free.RemoveWhere(n => builtins.Contains(n) || topLevelNames.Contains(n) || n == idName);
                                        bool moveable = free.Count == 0;
                                        var callerId = AddFunctionRecord(idName, recordStart, adjEnd, IsExported(source, (int)vdec.Range.Start), moveable);
                                        try
                                        {
                                            var rec = functions.Last();
                                            if (rec.TryGetValue("ext", out var extObj) && extObj is Dictionary<string, object> extDict)
                                            {
                                                extDict["identifiers.body"] = used.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["identifiers.free"] = (used.Count == 0) ? new List<string>() : free.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["declared.params"] = paramNames.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["declared.locals"] = declared.Except(paramNames, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["moveable"] = moveable;
                                            }
                                        }
                                        catch { }
                                        try { if (fe.Body != null) CollectCalls(fe.Body, callerId); } catch { }
                                    }
                                    else if (init is ArrowFunctionExpression af)
                                    {
                                        var span = af.Range; var spanStart = span.Start;
                                        // arrow functions may have block bodies or concise expressions
                                        int spanEnd;
                                        if (af.Body is BlockStatement bs) spanEnd = bs.Range.End;
                                        else spanEnd = (int)vdec.Range.End;
                                        var jsdoc = FindJSDocStart(source, spanStart);
                                        var recordStart = jsdoc >= 0 ? jsdoc : (int)vdec.Range.Start;
                                        var adjEnd = ValidateEnd(source, recordStart, spanEnd);
                                        // collect identifiers and compute free vars
                                        var paramNames = new List<string>();
                                        try { foreach (var p in af.Params) if (p is Identifier pid && !string.IsNullOrEmpty(pid.Name)) paramNames.Add(pid.Name); } catch {}
                                        var (used, declared) = (af.Body is BlockStatement bs2b)
                                            ? CollectIdents(bs2b, paramNames)
                                            : (new HashSet<string>(), new HashSet<string>());
                                        var free = new HashSet<string>(used, StringComparer.Ordinal);
                                        foreach (var d in declared) free.Remove(d);
                                        free.RemoveWhere(n => builtins.Contains(n) || topLevelNames.Contains(n) || n == idName);
                                        bool moveable = free.Count == 0;
                                        var callerId = AddFunctionRecord(idName, recordStart, adjEnd, IsExported(source, (int)vdec.Range.Start), moveable);
                                        try
                                        {
                                            var rec = functions.Last();
                                            if (rec.TryGetValue("ext", out var extObj) && extObj is Dictionary<string, object> extDict)
                                            {
                                                extDict["identifiers.body"] = used.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["identifiers.free"] = (used.Count == 0) ? new List<string>() : free.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["declared.params"] = paramNames.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["declared.locals"] = declared.Except(paramNames, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                                                extDict["moveable"] = moveable;
                                            }
                                        }
                                        catch { }
                                        try
                                        {
                                            if (af.Body is BlockStatement bs2) CollectCalls(bs2, callerId);
                                            else CollectCalls(af, callerId);
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }

                        // export default function
                        if (node is ExportDefaultDeclaration ed && ed.Declaration is FunctionDeclaration dfd)
                        {
                            var name = dfd.Id?.Name ?? "default";
                            var span = dfd.Range; var spanStart = span.Start;
                            var spanEnd = dfd.Body != null ? dfd.Body.Range.End : span.End;
                            var jsdoc = FindJSDocStart(source, spanStart);
                            var recordStart = jsdoc >= 0 ? jsdoc : spanStart;
                            var adjEnd = ValidateEnd(source, recordStart, spanEnd);
                            // collect identifiers and compute free vars
                            var paramNames = new List<string>();
                            try { foreach (var p in dfd.Params) if (p is Identifier pid && !string.IsNullOrEmpty(pid.Name)) paramNames.Add(pid.Name); } catch {}
                            var (used, declared) = dfd.Body != null ? CollectIdents(dfd.Body, paramNames) : (new HashSet<string>(), new HashSet<string>());
                            var free = new HashSet<string>(used, StringComparer.Ordinal);
                            foreach (var d in declared) free.Remove(d);
                            free.RemoveWhere(n => builtins.Contains(n) || topLevelNames.Contains(n) || n == name);
                            bool moveable = free.Count == 0;
                            var callerId = AddFunctionRecord(name, recordStart, adjEnd, true, moveable);
                            try
                            {
                                var rec = functions.Last();
                                if (rec.TryGetValue("ext", out var extObj) && extObj is Dictionary<string, object> extDict)
                                {
                                    extDict["identifiers.body"] = used.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["identifiers.free"] = (used.Count == 0) ? new List<string>() : free.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["declared.params"] = paramNames.OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["declared.locals"] = declared.Except(paramNames, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                                    extDict["moveable"] = moveable;
                                }
                            }
                            catch { }
                            try { if (dfd.Body != null) CollectCalls(dfd.Body, callerId); } catch { }
                        }
                    }
                    catch { /* continue on node errors */ }
                }

                // basic import collection via regex (keeps original behavior)
                try
                {
                    var importRe = new Regex("^\\s*import\\s+.+?from\\s+['\"](?<from>[^'\"]+)['\"];?", RegexOptions.Multiline);
                    foreach (Match m in importRe.Matches(source))
                    {
                        imports.Add(new Dictionary<string, object>
                        {
                            ["type"] = "import",
                            ["kind"] = "esm",
                            ["from"] = m.Groups["from"].Value,
                            ["named"] = new List<object>()
                        });
                    }
                }
                catch { }
            }
            catch (Exception)
            {
                // Fall back to the original text-scanner heuristics if parsing fails
                // function declarations
                try
                {
                    var importRe = new Regex("^\\s*import\\s+.+?from\\s+['\"](?<from>[^'\"]+)['\"];?", RegexOptions.Multiline);
                    foreach (Match m in importRe.Matches(source))
                    {
                        imports.Add(new Dictionary<string, object>
                        {
                            ["type"] = "import",
                            ["kind"] = "esm",
                            ["from"] = m.Groups["from"].Value,
                            ["named"] = new List<object>()
                        });
                    }
                }
                catch { }

                // function declarations (fallback)
                var funcDeclRe = new Regex(@"function\s+(?<name>[A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);
                foreach (Match m in funcDeclRe.Matches(source))
                {
                    var name = m.Groups["name"].Value;
                    var startSearch = m.Index;
                    var jsdoc = FindJSDocStart(source, startSearch);
                    var recordStart = jsdoc >= 0 ? jsdoc : startSearch;
                    var bracePos = source.IndexOf('{', m.Index + m.Length);
                    if (bracePos < 0) continue;
                    var endPos = source.Length; // best-effort: grab until EOF
                    AddFunctionRecord(name, recordStart, endPos, IsExported(source, startSearch), false);
                }

                // var/let/const assignments (fallback)
                var varAssignRe = new Regex(@"\b(var|let|const)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(async\s*)?(function|\(|[A-Za-z_$])", RegexOptions.Compiled);
                foreach (Match m in varAssignRe.Matches(source))
                {
                    var name = m.Groups["name"].Value;
                    var startSearch = m.Index;
                    var jsdoc = FindJSDocStart(source, startSearch);
                    var recordStart = jsdoc >= 0 ? jsdoc : startSearch;
                    var bracePos = source.IndexOf('{', m.Index + m.Length);
                    if (bracePos < 0)
                    {
                        var semi = source.IndexOfAny(new[] { ';', '\n' }, m.Index + m.Length);
                        var endPos = semi < 0 ? Math.Min(source.Length, m.Index + m.Length + 200) : semi + 1;
                        AddFunctionRecord(name, recordStart, endPos, IsExported(source, startSearch), false);
                    }
                    else
                    {
                        var endPos = source.Length; // best-effort
                        AddFunctionRecord(name, recordStart, endPos, IsExported(source, startSearch), false);
                    }
                }
            }

            // de-duplicate contained/overlapping function spans:
            // if a function span is fully contained inside another, drop it to avoid duplicated slices
            functions = functions
                .OrderBy(f =>
                {
                    try
                    {
                        if (f.TryGetValue("span", out var s) && s is Dictionary<string,int> spanDict && spanDict.TryGetValue("start", out var st)) return st;
                    }
                    catch { }
                    return int.MaxValue;
                })
                .ToList();

            var deduped = new List<Dictionary<string, object>>();
            foreach (var f in functions)
            {
                try
                {
                    if (!(f.TryGetValue("span", out var s) && s is Dictionary<string,int> spanDict)) { deduped.Add(f); continue; }
                    var start = spanDict.ContainsKey("start") ? spanDict["start"] : 0;
                    var end = spanDict.ContainsKey("end") ? spanDict["end"] : 0;
                    bool contained = false;
                    foreach (var k in deduped)
                    {
                        if (!(k.TryGetValue("span", out var ksObj) && ksObj is Dictionary<string,int> kspan)) continue;
                        var kstart = kspan.ContainsKey("start") ? kspan["start"] : 0;
                        var kend = kspan.ContainsKey("end") ? kspan["end"] : 0;
                        if (kstart <= start && end <= kend) { contained = true; break; }
                    }
                    if (!contained) deduped.Add(f);
                }
                catch { deduped.Add(f); }
            }

            functions = deduped;

            // Diagnostic: report findings and fail fast if nothing was detected.
            try
            {
                Console.WriteLine($"JsAnalyzer: detected {functions.Count} functions, {imports.Count} imports, {calls.Count} calls");
                if (functions.Count == 0)
                {
                    Console.WriteLine("JsAnalyzer: WARNING - no functions detected in source. This will produce an empty plan. Aborting to avoid silent failures.");
                    throw new InvalidOperationException("Analyzer found no functions in source; aborting pipeline.");
                }
            }
            catch (Exception ex)
            {
                // Surface analyzer errors to the caller while still writing whatever facts we have for inspection.
                Console.WriteLine("JsAnalyzer: error during diagnostics: " + ex.Message);
                throw;
            }

            // write facts
            var facts = new { functions = functions.Count, imports = imports.Count };
            File.WriteAllText(Path.Combine(_factsDir, "facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));

            var index = new Dictionary<string, object>
            {
                ["dfpVersion"] = "0.3",
                ["source"] = Path.GetFullPath(sourcePath),
                ["features"] = new List<string> { "imports.esm", "functions.core", "identifiers.body", "callgraph.basic" },
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["imports.esm"] = true,
                    ["functions.core"] = true,
                    ["identifiers.body"] = true,
                    ["callgraph.basic"] = true
                },
                ["shards"] = new List<object>
                {
                    new Dictionary<string, object>{ ["kind"]="functions", ["path"]="facts.d/functions.ndjson", ["count"]=functions.Count },
                    new Dictionary<string, object>{ ["kind"]="imports",   ["path"]="facts.d/imports.ndjson",   ["count"]=imports.Count },
                    new Dictionary<string, object>{ ["kind"]="calls",     ["path"]="facts.d/calls.ndjson",     ["count"]=calls.Count }
                },
                ["stats"] = new Dictionary<string,int>{ ["functionCount"] = functions.Count, ["importCount"] = imports.Count }
            };

            File.WriteAllText(Path.Combine(_factsDir, "facts.index.json"), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));

            var ddir = Path.Combine(_factsDir, "facts.d");
            Directory.CreateDirectory(ddir);
            File.WriteAllLines(Path.Combine(ddir, "functions.ndjson"), functions.Select(j => JsonSerializer.Serialize(j)));
            File.WriteAllLines(Path.Combine(ddir, "imports.ndjson"), imports.Select(j => JsonSerializer.Serialize(j)));
            File.WriteAllLines(Path.Combine(ddir, "calls.ndjson"), calls.Select(j => JsonSerializer.Serialize(j)));

            // persist top-level declared names for downstream linking/exports
            try
            {
                var tl = new { names = topLevelNames.OrderBy(s => s, StringComparer.Ordinal).ToArray() };
                File.WriteAllText(Path.Combine(_factsDir, "top-level.json"), JsonSerializer.Serialize(tl, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
