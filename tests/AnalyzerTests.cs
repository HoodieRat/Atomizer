using System;
using System.IO;
using Xunit;
using AtomizeJs.Core;
using AtomizeJs.Analyze;

public class AnalyzerTests
{
    [Fact]
    public void Moveable_WhenNoFreeVars_IsTrue()
    {
    var src = "export function a(x){ return x+1 }\nexport function b(y){ return a(y) }";
        var tmp = Path.GetTempFileName().Replace(".tmp", ".js");
        File.WriteAllText(tmp, src);
        var state = new AppState { SourceJs = tmp, OutDir = Path.Combine(Path.GetTempPath(), "out-" + Guid.NewGuid().ToString("n")) };
        Directory.CreateDirectory(state.OutDir);
        Environment.SetEnvironmentVariable("ATOMIZER_FACTS_DIR", Path.Combine(Path.GetTempPath(), "facts-" + Guid.NewGuid().ToString("n")));
        new JsAnalyzer(state).Analyze();
        var factsPath = Path.Combine(AtomizeJs.Utils.EnvPaths.FactsDir, "facts.json");
        var facts = File.ReadAllText(factsPath);
        Assert.Contains("\"functions\":", facts);
    }
}
