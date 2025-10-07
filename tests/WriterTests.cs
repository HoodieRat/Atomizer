using System;
using System.IO;
using Xunit;
using AtomizeJs.Core;
using AtomizeJs.Analyze;
using AtomizeJs.Plan;
using AtomizeJs.Write;

public class WriterTests
{
    [Fact]
    public void Original_Module_Has_Explicit_Exports()
    {
    // Make 'a' non-moveable by referencing an undefined free var 'x' so it stays in the 'original' module
    var src = "export function a(){ return x + 1 }\nfunction hidden(){ return 2 }";
        var tmp = Path.GetTempFileName().Replace(".tmp", ".js");
        File.WriteAllText(tmp, src);
        var outDir = Path.Combine(Path.GetTempPath(), "out-" + Guid.NewGuid().ToString("n"));
        var state = new AppState { SourceJs = tmp, OutDir = outDir, MaxFiles = 3, MinClusterSize = 2 };
    Environment.SetEnvironmentVariable("ATOMIZER_FACTS_DIR", Path.Combine(Path.GetTempPath(), "facts-" + Guid.NewGuid().ToString("n")));
    Environment.SetEnvironmentVariable("ATOMIZER_PLANS_DIR", Path.Combine(Path.GetTempPath(), "plans-" + Guid.NewGuid().ToString("n")));
    new JsAnalyzer(state).Analyze();
        new OllamaPlanner(state).Plan();
        new ModuleWriter(state).WriteModules();
        var baseName = Path.GetFileNameWithoutExtension(tmp);
        var originalPath = Path.Combine(outDir, baseName + ".atomized", baseName + ".js");
        var txt = File.ReadAllText(originalPath);
        Assert.Contains("export {", txt);
    }
}
