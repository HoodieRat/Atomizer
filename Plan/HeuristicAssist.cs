using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AtomizeJs.Plan
{
    public static class HeuristicAssist
    {
    public static object FallbackPlan(string indexJson, int maxFiles, int minClusterSize)
        {
            // Very small deterministic fallback: read function count from index and split evenly
            try
            {
                using var doc = JsonDocument.Parse(indexJson);
                // read all functions and their moveable flag from facts
                var functionsPath = Path.Combine(AtomizeJs.Utils.EnvPaths.FactsDir, "facts.d", "functions.ndjson");
                var moveableList = new List<string>();
                var nonMoveableList = new List<string>();
                var fnames = new Dictionary<string, string>();
                var spans = new Dictionary<string, (int start, int end)>();
                if (File.Exists(functionsPath))
                {
                    foreach (var line in File.ReadAllLines(functionsPath))
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(line);
                            var root = d.RootElement;
                            var id = root.GetProperty("id").GetString();
                            var name = root.GetProperty("name").GetString();
                            try
                            {
                                var span = root.GetProperty("span");
                                var st = span.GetProperty("start").GetInt32();
                                var en = span.GetProperty("end").GetInt32();
                                if (!string.IsNullOrEmpty(id)) spans[id!] = (st, en);
                            }
                            catch { }
                            bool moveable = true;
                            if (root.TryGetProperty("ext", out var ext) && ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("moveable", out var mv))
                            {
                                if (mv.ValueKind == JsonValueKind.False) moveable = false;
                            }
                            if (id != null) {
                                if (moveable) moveableList.Add(id);
                                else nonMoveableList.Add(id);
                            }
                            if (id != null && name != null) fnames[id] = name;
                        }
                        catch { }
                    }
                }

                var modules = new List<Dictionary<string, object>>();

                // If there are any non-moveable functions, keep them together in a module called 'original'
                if (nonMoveableList.Count > 0)
                {
                    var orig = new Dictionary<string, object>
                    {
                        ["name"] = "original",
                        ["slug"] = "original",
                        ["functions"] = nonMoveableList.Cast<object>().ToList()
                    };
                    modules.Add(orig);
                }

                // Now cluster moveable functions deterministically, honoring maxFiles overall and minClusterSize per module
                var fcount = moveableList.Count;
                if (fcount == 0) return new { modules };

                int reservedForOriginal = nonMoveableList.Count > 0 ? 1 : 0;
                int maxMoveableModules = Math.Max(1, Math.Max(0, maxFiles - reservedForOriginal));
                // initial desired groups based on minClusterSize
                int desiredBySize = Math.Max(1, (int)Math.Ceiling((double)fcount / Math.Max(1, minClusterSize)));
                int groups = Math.Min(maxMoveableModules, desiredBySize);

                // Distribute functions by approximate size across 'groups' using greedy bin-packing
                var buckets = new List<List<string>>();
                var bucketSizes = new List<int>();
                for (int i = 0; i < groups; i++) { buckets.Add(new List<string>()); bucketSizes.Add(0); }
                // order moveable by descending size
                var ordered = moveableList
                    .Select(id => new { id, size = spans.TryGetValue(id, out var sp) ? Math.Max(1, sp.end - sp.start) : 1 })
                    .OrderByDescending(x => x.size)
                    .ToList();
                foreach (var item in ordered)
                {
                    // find bucket with smallest current total size
                    int minIdx = 0; int minSize = bucketSizes[0];
                    for (int b = 1; b < bucketSizes.Count; b++)
                    {
                        if (bucketSizes[b] < minSize) { minIdx = b; minSize = bucketSizes[b]; }
                    }
                    buckets[minIdx].Add(item.id);
                    bucketSizes[minIdx] += item.size;
                }

                // Ensure we don't create a tiny last bucket below minClusterSize when avoidable: merge tail if needed
                if (groups > 1 && buckets[groups - 1].Count > 0 && buckets[groups - 1].Count < minClusterSize)
                {
                    // Merge the last bucket into the previous one
                    buckets[groups - 2].AddRange(buckets[groups - 1]);
                    buckets.RemoveAt(groups - 1);
                    groups--;
                }

                // Emit modules for each bucket
                for (int g = 0; g < buckets.Count; g++)
                {
                    var list = buckets[g];
                    if (list.Count == 0) continue;
                    var m = new Dictionary<string, object>
                    {
                        ["name"] = $"mod{g+1}",
                        ["slug"] = $"mod{g+1}",
                        ["functions"] = list
                    };
                    var first = list[0];
                    if (fnames.TryGetValue(first, out var fname) && !string.IsNullOrWhiteSpace(fname))
                    {
                        m["name"] = fname;
                        m["slug"] = Slugify(fname);
                    }
                    modules.Add(m);
                }

                return new { modules };
            }
            catch
            {
                return new { modules = new object[0] };
            }
        }

        static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "mod" + Guid.NewGuid().ToString("n").Substring(0,6);
            var slug = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(slug)) return "mod" + Guid.NewGuid().ToString("n").Substring(0,6);
            return slug;
        }
    }
}
