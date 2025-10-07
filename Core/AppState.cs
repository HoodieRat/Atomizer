using System;
using System.IO;
using System.Text.Json;

namespace AtomizeJs.Core
{
    public class AppState
    {
        public string? SourceJs { get; set; }
        public string OutDir { get; set; } = "out";
        public string Banner { get; set; } = "'use strict';";
        public int MaxFiles { get; set; } = 12;
        public int MinClusterSize { get; set; } = 3;
    public bool OutputDebug { get; set; } = false;
        public LlmSettings LLM { get; set; } = new LlmSettings();

        public static string DataDir => Path.Combine(Directory.GetCurrentDirectory(), "data");
        public static string AppStatePath => Path.Combine(DataDir, "appstate.json");

        public static AppState LoadOrCreate()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (File.Exists(AppStatePath))
                {
                    var json = File.ReadAllText(AppStatePath);
                    return JsonSerializer.Deserialize<AppState>(json) ?? CreateDefault();
                }
            }
            catch { }
            var s = CreateDefault();
            s.Save();
            return s;
        }

        public static AppState CreateDefault()
        {
            return new AppState();
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDir);
            var tmp = AppStatePath + ".tmp";
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppStatePath, true);
        }
    }

    public class LlmSettings
    {
        public bool Enabled { get; set; } = true;
        public string Provider { get; set; } = "Ollama";
        public string Endpoint { get; set; } = "http://127.0.0.1:11434";
        public string Model { get; set; } = "llama3.1:8b";
        public double Temperature { get; set; } = 0.2;
        public int MaxTokens { get; set; } = 2048;
        public bool SendSource { get; set; } = false;
        public int MaxRounds { get; set; } = 2;
    }
}
