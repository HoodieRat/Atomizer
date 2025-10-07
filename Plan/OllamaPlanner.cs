using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AtomizeJs.Core;

namespace AtomizeJs.Plan
{
    public class OllamaPlanner
    {
        private readonly AppState _state;

        public OllamaPlanner(AppState state)
        {
            _state = state;
            Directory.CreateDirectory(AtomizeJs.Utils.EnvPaths.PlansDir);
        }

        public void Plan()
        {
            // Load index
            var idxPath = Path.Combine(AtomizeJs.Utils.EnvPaths.FactsDir, "facts.index.json");
            if (!File.Exists(idxPath)) throw new InvalidOperationException("facts.index.json not found; run Analyze first");
            var index = File.ReadAllText(idxPath);

            if (_state.LLM.Enabled && _state.LLM.Provider == "Ollama")
            {
                Console.WriteLine($"Calling Ollama at {_state.LLM.Endpoint} model={_state.LLM.Model}...");
                try
                {
                    using var client = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
                    var systemMsg = new Dictionary<string, object> { ["role"] = "system", ["content"] = "Group functions by how they work together; strong caller/callee stickiness; avoid tiny modules; strict JSON only; you may reply with {needs:[...]} to request feature packs, or {modules:[...]} for the final plan." };
                    var userMsg = new Dictionary<string, object> { ["role"] = "user", ["content"] = JsonDocument.Parse(index).RootElement };
                    var chat = new Dictionary<string, object> { ["model"] = _state.LLM.Model, ["messages"] = new object[] { systemMsg, userMsg }, ["temperature"] = _state.LLM.Temperature };
                    var content = new StringContent(JsonSerializer.Serialize(chat), Encoding.UTF8, "application/json");
                    var resp = client.PostAsync(new Uri(new Uri(_state.LLM.Endpoint), "/api/chat"), content).GetAwaiter().GetResult();
                    var status = (int)resp.StatusCode;
                    var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Directory.CreateDirectory(AtomizeJs.Utils.EnvPaths.PlansDir);
                    var raw = new Dictionary<string, object>
                    {
                        ["timestamp"] = DateTime.UtcNow.ToString("o"),
                        ["status"] = status,
                        ["body"] = txt
                    };
                    File.WriteAllText(Path.Combine(AtomizeJs.Utils.EnvPaths.PlansDir, "plan.raw.json"), JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));

                    // Try to parse and validate the response: expect JSON with modules or an array
                    try
                    {
                        using var respDoc = JsonDocument.Parse(txt);
                        var root = respDoc.RootElement;
                        bool ok = false;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                            ok = true;
                        else if (root.ValueKind == JsonValueKind.Array)
                            ok = true;
                        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
                            ok = false;

                        if (ok)
                        {
                            File.WriteAllText(Path.Combine(AtomizeJs.Utils.EnvPaths.PlansDir, "plan.json"), txt);
                            Console.WriteLine("Ollama returned a valid plan and it was saved to plans/plan.json");
                            return;
                        }
                        Console.WriteLine("Ollama response not in expected format; falling back to heuristic planner. See plans/plan.raw.json for raw output.");
                        // otherwise fall through to fallback
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Failed to parse Ollama response: " + ex.Message + "; falling back.");
                    }
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(AtomizeJs.Utils.EnvPaths.PlansDir);
                    var rawErr = new Dictionary<string, object>
                    {
                        ["timestamp"] = DateTime.UtcNow.ToString("o"),
                        ["error"] = ex.Message,
                        ["stack"] = ex.StackTrace
                    };
                    File.WriteAllText(Path.Combine(AtomizeJs.Utils.EnvPaths.PlansDir, "plan.raw.json"), JsonSerializer.Serialize(rawErr, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("Ollama call failed: " + ex.Message + "; falling back to heuristic planner. See plans/plan.raw.json for details.");
                }
            }

            // Fallback heuristic planner
            var fallback = HeuristicAssist.FallbackPlan(index, _state.MaxFiles, _state.MinClusterSize);
            File.WriteAllText(Path.Combine(AtomizeJs.Utils.EnvPaths.PlansDir, "plan.json"), JsonSerializer.Serialize(fallback, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
