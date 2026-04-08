using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemPalace.Core;

namespace MemPalace.Benchmarks;

/// <summary>
/// MemPalace × ConvoMem Benchmark
/// ================================
/// Mirrors Python benchmarks/convomem_bench.py
///
/// ConvoMem (Salesforce): 75,336 QA pairs across 6 evidence categories.
/// Downloads evidence files from HuggingFace on first run.
///
/// Usage:
///   mempalace-bench convomem [--limit N] [--category X] [--cache-dir DIR]
/// </summary>
public static class ConvoMemRunner
{
    private const string HfBase =
        "https://huggingface.co/datasets/Salesforce/ConvoMem/resolve/main/core_benchmark/evidence_questions";

    private static readonly Dictionary<string, string> Categories = new()
    {
        ["user_evidence"]              = "User Facts",
        ["assistant_facts_evidence"]   = "Assistant Facts",
        ["changing_evidence"]          = "Changing Facts",
        ["abstention_evidence"]        = "Abstention",
        ["preference_evidence"]        = "Preferences",
        ["implicit_connection_evidence"] = "Implicit Connections",
    };

    public static async Task RunAsync(
        int     limit     = 100,
        string? category  = null,
        string  cacheDir  = "",
        int     topK      = 5)
    {
        if (string.IsNullOrEmpty(cacheDir))
            cacheDir = Path.Combine(Path.GetTempPath(), "mempalace_convomem_cache");
        Directory.CreateDirectory(cacheDir);

        var cats = category is not null ? [category] : Categories.Keys.ToList();

        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine("  MemPalace × ConvoMem (C#)");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"  Categories:  {string.Join(", ", cats)}");
        Console.WriteLine($"  Limit:       {limit}");
        Console.WriteLine($"  Top-k:       {topK}");
        Console.WriteLine($"  Cache dir:   {cacheDir}");
        BenchmarkHelpers.Sep();
        Console.WriteLine();

        int totalHit = 0, totalItems = 0;
        var byCat = new Dictionary<string, (int hit, int total)>();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("mempalace-bench", "1.0"));

        foreach (var cat in cats)
        {
            var items = await LoadCategoryAsync(http, cat, cacheDir, limit);
            Console.WriteLine($"  [{cat}] loaded {items.Count} items");

            foreach (var item in items)
            {
                var (db, tempDir) = BenchmarkHelpers.MakeTempDb();
                try
                {
                    // Index all conversation messages
                    int gi = 0;
                    var docs = item.Messages.Select(msg => (
                        id:     $"{cat}_{item.Id}_{gi++}",
                        text:   msg,
                        domain: cat,
                        topic:  "message"));

                    int nIndexed = BenchmarkHelpers.IndexDocuments(db, docs);
                    if (nIndexed < 1) continue;

                    var retrieved = db.SearchFts5(item.Question, limit: topK);
                    var retrievedTexts = retrieved.Select(r => r.Snippet).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // Hit = any evidence message appears in top-K snippets
                    bool hit = item.EvidenceMessages.Any(ev =>
                        retrievedTexts.Any(t => t.Contains(ev, StringComparison.OrdinalIgnoreCase)));

                    if (hit) totalHit++;
                    totalItems++;

                    if (!byCat.TryGetValue(cat, out var s)) s = (0, 0);
                    byCat[cat] = hit ? (s.hit + 1, s.total + 1) : (s.hit, s.total + 1);
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        double overall = totalItems > 0 ? totalHit / (double)totalItems * 100 : 0;
        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"  RESULTS — MemPalace on ConvoMem (top-{topK})");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"\n  Overall R@{topK}: {overall:F1}%  ({totalHit}/{totalItems})\n");
        Console.WriteLine("  By category:");
        foreach (var (cat, (hit, total)) in byCat.OrderBy(kv => kv.Key))
        {
            double pct = total > 0 ? hit / (double)total * 100 : 0;
            Console.WriteLine($"    {cat,-34} {pct,5:F1}%  ({hit}/{total})");
        }
        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private sealed record ConvoMemItem(
        string             Id,
        string             Question,
        List<string>       Messages,
        List<string>       EvidenceMessages);

    private static async Task<List<ConvoMemItem>> LoadCategoryAsync(
        HttpClient http,
        string     cat,
        string     cacheDir,
        int        limit)
    {
        var catDir = Path.Combine(cacheDir, cat);
        Directory.CreateDirectory(catDir);

        // Discover or download the first available file for this category
        string localPattern = Path.Combine(catDir, "*.json");
        var localFiles = Directory.GetFiles(catDir, "*.json");

        if (localFiles.Length == 0)
        {
            // Try to download an index file listing available filenames
            var indexUrl  = $"{HfBase}/{cat}/";
            var indexFile = Path.Combine(catDir, "_index.html");
            try
            {
                var html = await http.GetStringAsync(indexUrl);
                await File.WriteAllTextAsync(indexFile, html);
            }
            catch
            {
                Console.WriteLine($"    (no data available for {cat} — skipping)");
                return [];
            }
            localFiles = Directory.GetFiles(catDir, "*.json");
        }

        var items = new List<ConvoMemItem>();
        foreach (var file in localFiles)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var node = JsonNode.Parse(stream);
                if (node is JsonArray arr)
                {
                    foreach (var elem in arr.OfType<JsonObject>())
                    {
                        var parsed = ParseItem(elem, cat);
                        if (parsed is not null) items.Add(parsed);
                        if (limit > 0 && items.Count >= limit) break;
                    }
                }
                else if (node is JsonObject obj)
                {
                    var parsed = ParseItem(obj, cat);
                    if (parsed is not null) items.Add(parsed);
                }
            }
            catch { /* skip malformed files */ }

            if (limit > 0 && items.Count >= limit) break;
        }

        return items;
    }

    private static ConvoMemItem? ParseItem(JsonObject obj, string cat)
    {
        var question = obj["question"]?.GetValue<string>()
                    ?? obj["query"]?.GetValue<string>()
                    ?? "";
        if (string.IsNullOrEmpty(question)) return null;

        var messages = new List<string>();
        if (obj["messages"] is JsonArray msgs)
            messages.AddRange(msgs.Select(m => m?.GetValue<string>() ?? "").Where(s => s.Length > 0));
        else if (obj["conversation"] is JsonArray conv)
            messages.AddRange(conv.OfType<JsonObject>()
                .Select(m => m["content"]?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0));

        var evidence = new List<string>();
        if (obj["evidence_messages"] is JsonArray evMsgs)
            evidence.AddRange(evMsgs.Select(m => m?.GetValue<string>() ?? "").Where(s => s.Length > 0));
        else if (obj["evidence"] is JsonArray ev)
            evidence.AddRange(ev.OfType<JsonObject>()
                .Select(m => m["content"]?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0));

        return new ConvoMemItem(
            obj["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
            question,
            messages,
            evidence);
    }
}
