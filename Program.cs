using System;
using System.IO;
using AtomizeJs.Core;
using AtomizeJs.Analyze;
using AtomizeJs.Plan;
using AtomizeJs.Write;
using AtomizeJs.Link;
using AtomizeJs.Health;

class Program
{
    static bool NonInteractive = false;
    static void Main(string[] args)
    {
        AppState state = AppState.LoadOrCreate();

        // CLI verbs and options
        if (args != null && args.Length > 0)
        {
            // option overrides
            foreach (var a in args)
            {
                if (a.StartsWith("--max-files=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(a.Substring("--max-files=".Length), out var mf)) { state.MaxFiles = mf; state.Save(); }
                }
                if (a.StartsWith("--min-cluster-size=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(a.Substring("--min-cluster-size=".Length), out var mcs)) { state.MinClusterSize = mcs; state.Save(); }
                }
            }

            bool has(string v) => Array.Exists(args, x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
            if (has("analyze") || has("plan") || has("write") || has("link") || has("run-pipeline") || has("plan-only") || has("write-only"))
            {
                NonInteractive = true;
                try
                {
                    if (has("analyze")) RunAnalyze(state);
                    if (has("plan") || has("plan-only")) RunPlan(state);
                    if (has("write") || has("write-only")) RunWrite(state);
                    if (has("link")) RunLink(state);
                    if (has("run-pipeline")) RunFullPipeline(state);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
                return;
            }
        }

        // Non-interactive mode: run full pipeline once and exit
        // Triggers:
        //   - command-line arg contains "run-pipeline" (any position)
        //   - environment variable ATOMIZER_AUTO=1
        var autoEnv = Environment.GetEnvironmentVariable("ATOMIZER_AUTO");
        bool wantsAuto = (!string.IsNullOrEmpty(autoEnv) && autoEnv == "1")
                         || (args != null && args.Length > 0 && Array.Exists(args, a => string.Equals(a, "run-pipeline", StringComparison.OrdinalIgnoreCase)));
        if (wantsAuto)
        {
            NonInteractive = true;
            try
            {
                RunFullPipeline(state);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error during pipeline: " + ex.Message);
            }
            return;
        }

        while (true)
        {
            Console.Clear();
            Console.WriteLine("Current settings:");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine();
            Console.WriteLine(" 1) Set Source JS file");
            Console.WriteLine(" 2) Set Output Directory");
            Console.WriteLine(" 3) Generate facts.json (Analyze)");
            Console.WriteLine(" 4) Propose plan with Ollama (Plan)");
            Console.WriteLine(" 5) Write modules (Emit)");
            Console.WriteLine(" 6) Link & fix cross-module refs");
            Console.WriteLine(" 7) Full pipeline: Analyze → Plan → Write → Link → Smoke");
            Console.WriteLine(" 8) View current settings (raw JSON)");
            Console.WriteLine(" 9) Reset settings (with confirmation)");
            Console.WriteLine(" 0) Exit");
            Console.WriteLine();
            Console.Write("Choose: ");
            var key = Console.ReadLine();
            try
            {
                switch (key)
                {
                    case "1":
                        Console.Write("Source JS path: ");
                        state.SourceJs = Console.ReadLine() ?? string.Empty;
                        state.Save();
                        break;
                    case "2":
                        Console.Write("Out directory: ");
                        state.OutDir = Console.ReadLine() ?? state.OutDir;
                        state.Save();
                        break;
                    case "3":
                        RunAnalyze(state);
                        break;
                    case "4":
                        RunPlan(state);
                        break;
                    case "5":
                        RunWrite(state);
                        break;
                    case "6":
                        RunLink(state);
                        break;
                    case "7":
                        RunFullPipeline(state);
                        break;
                    case "8":
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine("Press enter"); Console.ReadLine();
                        break;
                    case "9":
                        Console.Write("Are you sure you want to reset settings? (yes): ");
                        var yn = Console.ReadLine();
                        if (yn?.ToLowerInvariant() == "yes")
                        {
                            state = AppState.CreateDefault();
                            state.Save();
                        }
                        break;
                    case "0":
                        return;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Console.WriteLine("Press enter"); Console.ReadLine();
            }
        }
    }

    static void RunAnalyze(AppState state)
    {
        if (string.IsNullOrEmpty(state.SourceJs) || !File.Exists(state.SourceJs))
        {
            Console.WriteLine("SourceJs not set or does not exist"); Console.ReadLine(); return;
        }

        // Shim detection: if the source file looks like the shim that re-exports the atomized bridge,
        // prompt the user (interactive) or auto-restore from timestamped backup (non-interactive) to avoid analyzing the shim.
        try
        {
            var srcText = File.ReadAllText(state.SourceJs);
            bool looksLikeShim = srcText.Contains("Shim re-exporting atomized bridge") || srcText.Contains(".atomized.js") && srcText.Contains("export * from");
            if (looksLikeShim)
            {
                Console.WriteLine("Warning: source file appears to be an atomizer shim (re-export of atomized bridge).\nAnalyzing the shim will produce zero functions.");
                var inputDir = Path.GetDirectoryName(state.SourceJs) ?? ".";
                var baseName = Path.GetFileNameWithoutExtension(state.SourceJs) ?? "";
                var backups = Directory.GetFiles(inputDir, baseName + "_old*.js").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToArray();
                if (backups.Length == 0)
                {
                    Console.WriteLine($"No timestamped backups ({baseName}_old*.js) found in the input directory. Please restore your original source before analyzing.");
                    if (!NonInteractive) { Console.WriteLine("Press enter"); Console.ReadLine(); }
                    return;
                }

                // Prefer a backup that does NOT look like a shim and has reasonable size.
                string ChooseBackup(string[] files)
                {
                    foreach (var f in files)
                    {
                        try
                        {
                            var txt = File.ReadAllText(f);
                            var size = new FileInfo(f).Length;
                            var looksLikeShimInner = txt.Contains("Shim re-exporting atomized bridge") || (txt.Contains(".atomized.js") && txt.Contains("export * from"));
                            if (!looksLikeShimInner && size > 400) return f;
                        }
                        catch { }
                    }
                    // fallback: return largest file
                    return files.OrderByDescending(f => new FileInfo(f).Length).First();
                }

                var latest = ChooseBackup(backups);
                if (latest == null)
                {
                    Console.WriteLine("No suitable backup found to restore.");
                    if (!NonInteractive) { Console.WriteLine("Press enter"); Console.ReadLine(); }
                    return;
                }

                if (NonInteractive)
                {
                    File.Copy(latest, state.SourceJs, true);
                    Console.WriteLine($"Auto-restored original from backup: {Path.GetFileName(latest)} -> {Path.GetFileName(state.SourceJs)}");
                }
                else
                {
                    Console.Write($"Restore latest backup '{Path.GetFileName(latest)}' to '{Path.GetFileName(state.SourceJs)}'? (yes/no): ");
                    var yn = Console.ReadLine();
                    if (yn?.ToLowerInvariant() == "yes")
                    {
                        File.Copy(latest, state.SourceJs, true);
                        Console.WriteLine($"Restored {Path.GetFileName(latest)} -> {Path.GetFileName(state.SourceJs)}");
                    }
                    else
                    {
                        Console.WriteLine("Analyze aborted. Restore the original source before running the analyzer.");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Shim-detection step failed: " + ex.Message);
            // continue — fall back to normal analyze which will fail fast if file is shim
        }

        var analyzer = new JsAnalyzer(state);
        analyzer.Analyze();
        Console.WriteLine("Facts generated."); if (!NonInteractive) { Console.ReadLine(); }
    }

    static void RunPlan(AppState state)
    {
    var planner = new OllamaPlanner(state);
        planner.Plan();
        Console.WriteLine("Plan written."); if (!NonInteractive) { Console.ReadLine(); }
    }

    static void RunWrite(AppState state)
    {
        var writer = new ModuleWriter(state);
        writer.WriteModules();
        Console.WriteLine("Modules written."); if (!NonInteractive) { Console.ReadLine(); }
    }

    static void RunLink(AppState state)
    {
        var rewriter = new DependencyRewriter(state);
        rewriter.Rewrite();
        Console.WriteLine("Linking complete."); if (!NonInteractive) { Console.ReadLine(); }
    }

    static void RunFullPipeline(AppState state)
    {
        RunAnalyze(state);
        RunPlan(state);
        RunWrite(state);
        RunLink(state);
        var smoke = new Smoke(state);
        var ok = smoke.RunChecks();
        Console.WriteLine(ok ? "Smoke checks passed." : "Smoke checks failed. See messages above.");
        // print a short summary report if available
        PrintPipelineSummary();
        try
        {
            GenerateHtmlReport(state);
        }
        catch { }
        if (!NonInteractive) { Console.ReadLine(); }
    }

    static void PrintPipelineSummary()
    {
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "plans", "report.json");
            if (!File.Exists(path)) { Console.WriteLine("No plans/report.json found for summary."); return; }
            var txt = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(txt);
            var arr = doc.RootElement;
            int total = arr.GetArrayLength();
            int moveable = 0; int nonmove = 0;
            var moduleCounts = new Dictionary<string,int>();
            foreach (var it in arr.EnumerateArray())
            {
                var mv = it.GetProperty("moveable").GetBoolean();
                if (mv) moveable++; else nonmove++;
                var mod = it.GetProperty("module").GetString() ?? "";
                if (!moduleCounts.ContainsKey(mod)) moduleCounts[mod] = 0;
                moduleCounts[mod]++;
            }
            Console.WriteLine($"Pipeline summary: total functions={total}, moveable={moveable}, kept_in_original={nonmove}");
            Console.WriteLine("Top modules:");
            foreach (var kv in moduleCounts.OrderByDescending(k => k.Value).Take(10)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to print pipeline summary: " + ex.Message);
        }
    }

    static void GenerateHtmlReport(AppState state)
    {
        try
        {
            var planPath = Path.Combine(Directory.GetCurrentDirectory(), "plans", "plan.json");
            if (!File.Exists(planPath)) return;
            var plan = System.Text.Json.JsonDocument.Parse(File.ReadAllText(planPath)).RootElement;
            var modules = plan.GetProperty("modules");
            var outRoot = Path.Combine(Directory.GetCurrentDirectory(), state.OutDir);
            var baseName = string.IsNullOrEmpty(state.SourceJs) ? "source" : Path.GetFileNameWithoutExtension(state.SourceJs);
            var groupDir = Path.Combine(outRoot, baseName + ".atomized");
            var rows = new System.Text.StringBuilder();
            foreach (var m in modules.EnumerateArray())
            {
                var slug = m.GetProperty("slug").GetString() ?? "";
                var count = m.GetProperty("functions").GetArrayLength();
                string fpath = string.Equals(slug, "original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(groupDir, baseName + ".js")
                    : Path.Combine(groupDir, slug + ".js");
                long size = File.Exists(fpath) ? new FileInfo(fpath).Length : 0;
                rows.AppendLine($"<tr><td>{System.Web.HttpUtility.HtmlEncode(slug)}</td><td>{count}</td><td>{size}</td></tr>");
            }

            var originalList = new System.Text.StringBuilder();
            var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "plans", "report.json");
            if (File.Exists(reportPath))
            {
                var txt = File.ReadAllText(reportPath);
                var arr = System.Text.Json.JsonDocument.Parse(txt).RootElement;
                foreach (var it in arr.EnumerateArray())
                {
                    bool mv = it.GetProperty("moveable").GetBoolean();
                    if (!mv)
                    {
                        var name = it.GetProperty("name").GetString() ?? "";
                        originalList.AppendLine($"<li>{System.Web.HttpUtility.HtmlEncode(name)}</li>");
                    }
                }
            }

            var tmplPath = Path.Combine(Directory.GetCurrentDirectory(), "tools", "report.html.template");
            if (!File.Exists(tmplPath)) return;
            var html = File.ReadAllText(tmplPath)
                .Replace("{{SOURCE}}", System.Web.HttpUtility.HtmlEncode(state.SourceJs ?? ""))
                .Replace("{{ROWS}}", rows.ToString())
                .Replace("{{ORIGINAL_LIST}}", originalList.ToString());
            var outHtml = Path.Combine(Directory.GetCurrentDirectory(), "output", "report.html");
            Directory.CreateDirectory(Path.GetDirectoryName(outHtml)!);
            File.WriteAllText(outHtml, html);
        }
        catch { }
    }
}
