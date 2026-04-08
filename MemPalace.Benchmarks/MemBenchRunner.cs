using System.Text.Json;
using System.Text.Json.Nodes;
using MemPalace.Core;

namespace MemPalace.Benchmarks;

/// <summary>
/// MemPalace × MemBench Benchmark
/// ================================
/// Mirrors Python benchmarks/membench_bench.py
///
/// MemBench (ACL 2025): https://aclanthology.org/2025.findings-acl.989/
/// Data:               https://github.com/import-myself/Membench
///
/// Measures RETRIEVAL RECALL: is the answer-relevant turn in the top-K retrieved?
///
/// Usage (via MemPalace.Benchmarks CLI):
///   mempalace-bench membench &lt;data_dir&gt; [--category X] [--topic X] [--top-k N] [--mode raw|hybrid]
/// </summary>
public static class MemBenchRunner
{
    private static readonly Dictionary<string, string> CategoryFiles = new()
    {
        ["simple"]           = "simple.json",
        ["highlevel"]        = "highlevel.json",
        ["knowledge_update"] = "knowledge_update.json",
        ["comparative"]      = "comparative.json",
        ["conditional"]      = "conditional.json",
        ["noisy"]            = "noisy.json",
        ["aggregative"]      = "aggregative.json",
        ["highlevel_rec"]    = "highlevel_rec.json",
        ["lowlevel_rec"]     = "lowlevel_rec.json",
        ["RecMultiSession"]  = "RecMultiSession.json",
        ["post_processing"]  = "post_processing.json",
    };

    public static void Run(
        string dataDir,
        string? category   = null,
        string  topic      = "movie",
        int     topK       = 5,
        int     limit      = 0,
        string  mode       = "hybrid",
        string? outFile    = null)
    {
        var items = LoadMemBench(dataDir, category is null ? null : [category], topic, limit);
        if (items.Count == 0)
        {
            Console.WriteLine($"No items found in {dataDir}");
            return;
        }

        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine("  MemPalace × MemBench (C#)");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"  Data dir:    {dataDir}");
        Console.WriteLine($"  Category:    {category ?? "all"}");
        Console.WriteLine($"  Topic:       {topic}");
        Console.WriteLine($"  Items:       {items.Count}");
        Console.WriteLine($"  Top-k:       {topK}");
        Console.WriteLine($"  Mode:        {mode}");
        BenchmarkHelpers.Sep();
        Console.WriteLine();

        var byCat     = new Dictionary<string, (int hit, int total)>();
        int totalHit  = 0;
        var results   = new List<object>();

        for (int idx = 0; idx < items.Count; idx++)
        {
            var item    = items[idx];
            var itemKey = $"{item.Category}_{item.Topic}_{idx + 1}";

            var (db, tempDir) = BenchmarkHelpers.MakeTempDb();
            try
            {
                // Index all turns
                var docs    = BuildTurnDocs(item.Turns, itemKey);
                int nIndexed = BenchmarkHelpers.IndexDocuments(db, docs);
                if (nIndexed < 1) continue;

                int nRetrieve = Math.Min(mode == "hybrid" ? topK * 3 : topK, nIndexed);

                // Retrieve via FTS5
                var retrieved = db.SearchFts5(item.Question, limit: nRetrieve);
                var retrievedIds = retrieved.Select(r => r.Id).ToList();
                var retrievedDocs = retrieved.Select(r => r.Snippet).ToList();

                if (mode == "hybrid")
                {
                    var names        = BenchmarkHelpers.PersonNames(item.Question);
                    var nameWords    = new HashSet<string>(names.Select(n => n.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
                    var allKws       = BenchmarkHelpers.Keywords(item.Question);
                    var predicateKws = allKws.Where(w => !nameWords.Contains(w)).ToList();

                    var scored = retrieved
                        .Select((r, i) => (
                            score:   r.Score * (1.0 - 0.5 * BenchmarkHelpers.KeywordOverlap(predicateKws, retrievedDocs[i])),
                            id:      r.Id,
                            snippet: r.Snippet))
                        .OrderByDescending(x => x.score)
                        .Take(topK)
                        .ToList();

                    retrievedIds = scored.Select(x => x.id).ToList();
                }
                else
                {
                    retrievedIds = retrievedIds.Take(topK).ToList();
                }

                // Check hit: does any target turn appear in results?
                var targetIds = new HashSet<string>();
                foreach (var step in item.TargetStepIds)
                    if (step is JsonArray arr && arr.Count > 0)
                        targetIds.Add($"{itemKey}_g{arr[0]}");

                bool hit = targetIds.Overlaps(retrievedIds);
                if (hit) totalHit++;

                if (!byCat.TryGetValue(item.Category, out var catStat))
                    catStat = (0, 0);
                byCat[item.Category] = hit
                    ? (catStat.hit + 1, catStat.total + 1)
                    : (catStat.hit, catStat.total + 1);

                results.Add(new
                {
                    category       = item.Category,
                    topic          = item.Topic,
                    question       = item.Question,
                    ground_truth   = item.GroundTruth,
                    target_ids     = targetIds.ToList(),
                    retrieved_ids  = retrievedIds,
                    hit_at_k       = hit,
                });

                if ((idx + 1) % 50 == 0)
                {
                    double running = totalHit / (double)(idx + 1) * 100;
                    Console.WriteLine($"  [{idx + 1,4}/{items.Count}]  running R@{topK}: {running:F1}%");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        double overall = items.Count > 0 ? totalHit / (double)items.Count * 100 : 0;
        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"  RESULTS — MemPalace on MemBench ({mode} mode, top-{topK})");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"\n  Overall R@{topK}: {overall:F1}%  ({totalHit}/{items.Count})\n");
        Console.WriteLine("  By category:");
        foreach (var (cat, (hit, total)) in byCat.OrderBy(kv => kv.Key))
        {
            double pct = total > 0 ? hit / (double)total * 100 : 0;
            Console.WriteLine($"    {cat,-20} {pct,5:F1}%  ({hit}/{total})");
        }
        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine();

        if (outFile is not null)
        {
            File.WriteAllText(outFile, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Results saved to: {outFile}");
        }
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private sealed record MemBenchItem(
        string            Category,
        string            Topic,
        int               Tid,
        JsonArray         Turns,
        string            Question,
        JsonObject        Choices,
        string            GroundTruth,
        string            AnswerText,
        JsonArray         TargetStepIds);

    private static List<MemBenchItem> LoadMemBench(
        string   dataDir,
        IReadOnlyList<string>? categories,
        string   topic,
        int      limit)
    {
        var cats = categories?.Count > 0 ? categories : CategoryFiles.Keys.ToList();
        var items = new List<MemBenchItem>();

        foreach (var cat in cats)
        {
            if (!CategoryFiles.TryGetValue(cat, out var fname)) continue;
            var fpath = Path.Combine(dataDir, fname);
            if (!File.Exists(fpath)) continue;

            using var stream = File.OpenRead(fpath);
            var raw = JsonNode.Parse(stream) as JsonObject;
            if (raw is null) continue;

            foreach (var (topicKey, topicItems) in raw)
            {
                if (!string.IsNullOrEmpty(topic) && topicKey != topic && topicKey != "roles" && topicKey != "events")
                    continue;
                if (topicItems is not JsonArray arr) continue;

                foreach (var node in arr)
                {
                    if (node is not JsonObject item) continue;
                    var turns = item["message_list"] as JsonArray ?? [];
                    var qa    = item["QA"] as JsonObject ?? [];
                    if (turns.Count == 0 || qa.Count == 0) continue;

                    items.Add(new MemBenchItem(
                        cat,
                        topicKey,
                        (int)(item["tid"]?.GetValue<int>() ?? 0),
                        turns,
                        qa["question"]?.GetValue<string>() ?? "",
                        qa["choices"] as JsonObject ?? [],
                        qa["ground_truth"]?.GetValue<string>() ?? "",
                        qa["answer"]?.GetValue<string>() ?? "",
                        qa["target_step_id"] as JsonArray ?? []));
                }
            }
        }

        return limit > 0 ? items.Take(limit).ToList() : items;
    }

    private static IEnumerable<(string id, string text, string domain, string topic)> BuildTurnDocs(
        JsonArray messageList, string itemKey)
    {
        // message_list can be flat (list of dicts) or nested (list of sessions)
        bool isFlat = messageList.Count > 0 && messageList[0] is JsonObject;
        var sessions = isFlat ? [messageList] : messageList.OfType<JsonArray>().ToList();

        int globalIdx = 0;
        foreach (var session in sessions)
        {
            foreach (var turnNode in session)
            {
                if (turnNode is not JsonObject turn) continue;
                var user  = turn["user"]?.GetValue<string>()  ?? turn["user_message"]?.GetValue<string>()  ?? "";
                var asst  = turn["assistant"]?.GetValue<string>() ?? turn["assistant_message"]?.GetValue<string>() ?? "";
                var time  = turn["time"]?.GetValue<string>()  ?? "";
                var text  = $"[User] {user} [Assistant] {asst}";
                if (!string.IsNullOrEmpty(time)) text = $"[{time}] " + text;

                var id = $"{itemKey}_g{globalIdx}";
                yield return (id, text, itemKey, "turn");
                globalIdx++;
            }
        }
    }
}
