using System;
using System.IO;
using Xunit;
using AtomizeJs.Core;
using AtomizeJs.Analyze;
using AtomizeJs.Plan;
using AtomizeJs.Write;
using AtomizeJs.Link;

public class LinkerTests
{
    [Fact]
    public void Linker_Adds_Imports_For_Cross_Module_Calls()
    {
        var src = "export function a(){ return 1 }\nexport function b(){ return a() }";
        var tmp = Path.GetTempFileName().Replace(".tmp", ".js");
        File.WriteAllText(tmp, src);
        var outDir = Path.Combine(Path.GetTempPath(), "out-" + Guid.NewGuid().ToString("n"));
        var state = new AppState { SourceJs = tmp, OutDir = outDir, MaxFiles = 2, MinClusterSize = 1 };
    Environment.SetEnvironmentVariable("ATOMIZER_FACTS_DIR", Path.Combine(Path.GetTempPath(), "facts-" + Guid.NewGuid().ToString("n")));
    Environment.SetEnvironmentVariable("ATOMIZER_PLANS_DIR", Path.Combine(Path.GetTempPath(), "plans-" + Guid.NewGuid().ToString("n")));
    new JsAnalyzer(state).Analyze();
        new OllamaPlanner(state).Plan();
        // Force a two-module plan so that b calls a across modules
        var factsDir = AtomizeJs.Utils.EnvPaths.FactsDir;
        var funcsPath = Path.Combine(factsDir, "facts.d", "functions.ndjson");
        string idA = "", idB = "";
        foreach (var line in File.ReadAllLines(funcsPath))
        {
            using var d = System.Text.Json.JsonDocument.Parse(line);
            var name = d.RootElement.GetProperty("name").GetString();
            var id = d.RootElement.GetProperty("id").GetString() ?? "";
            if (name == "a") idA = id; else if (name == "b") idB = id;
        }
        Assert.False(string.IsNullOrEmpty(idA) || string.IsNullOrEmpty(idB));
        var planDir = AtomizeJs.Utils.EnvPaths.PlansDir;
        var forced = new {
            modules = new object[] {
                new { name = "modA", slug = "modA", functions = new [] { idA } },
                new { name = "modB", slug = "modB", functions = new [] { idB } }
            }
        };
        File.WriteAllText(Path.Combine(planDir, "plan.json"), System.Text.Json.JsonSerializer.Serialize(forced));
        new ModuleWriter(state).WriteModules();
        new DependencyRewriter(state).Rewrite();
        var group = Path.Combine(outDir, Path.GetFileNameWithoutExtension(tmp) + ".atomized");
        var modBPath = Path.Combine(group, "modB.js");
        Assert.True(File.Exists(modBPath));
        var content = File.ReadAllText(modBPath);
        Assert.Contains("import { a }", content);
    }
}
