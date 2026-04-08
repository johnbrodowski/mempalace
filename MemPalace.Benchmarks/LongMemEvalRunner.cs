using System.Text.Json;
using System.Text.Json.Nodes;
using MemPalace.Core;

namespace MemPalace.Benchmarks;

/// <summary>
/// MemPalace × LongMemEval Benchmark
/// =====================================
/// Mirrors Python benchmarks/longmemeval_bench.py (core retrieval modes only).
///
/// LongMemEval: long-form memory evaluation with sessions spanning many turns.
/// Data format: JSON array of items, each with a "session" (list of {role, content} turns)
/// and a "question" with "answer" and optionally "evidence_session_ids".
///
/// Usage:
///   mempalace-bench longmemeval &lt;data_file.json&gt; [--top-k N] [--mode raw|hybrid] [--limit N]
/// </summary>
public static class LongMemEvalRunner
{
    public static void Run(
        string  dataFile,
        int     topK    = 5,
        string  mode    = "hybrid",
        int     limit   = 0,
        string? outFile = null)
    {
        if (!File.Exists(dataFile))
        {
            Console.WriteLine($"File not found: {dataFile}");
            return;
        }

        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine("  MemPalace × LongMemEval (C#)");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"  Data file: {dataFile}");
        Console.WriteLine($"  Top-k:     {topK}");
        Console.WriteLine($"  Mode:      {mode}");
        Console.WriteLine($"  Limit:     {(limit > 0 ? limit.ToString() : "all")}");
        BenchmarkHelpers.Sep();
        Console.WriteLine();

        var items = LoadLongMemEval(dataFile, limit);
        if (items.Count == 0)
        {
            Console.WriteLine("No items found.");
            return;
        }

        int totalHit = 0;
        var results  = new List<object>();

        for (int i = 0; i < items.Count; i++)
        {
            var item    = items[i];
            var itemKey = $"lme_{i}";

            var (db, tempDir) = BenchmarkHelpers.MakeTempDb();
            try
            {
                // Index session turns
                int gi = 0;
                var docs = item.Turns.Select(t => (
                    id:     $"{itemKey}_{gi++}",
                    text:   $"[{t.role}] {t.content}",
                    domain: itemKey,
                    topic:  t.role));
                BenchmarkHelpers.IndexDocuments(db, docs);

                int nRetrieve = mode == "hybrid" ? topK * 3 : topK;
                var retrieved      = db.SearchFts5(item.Question, limit: nRetrieve);
                var retrievedIds   = retrieved.Select(r => r.Id).ToList();
                var retrievedTexts = retrieved.Select(r => r.Snippet).ToList();

                if (mode == "hybrid")
                {
                    var allKws  = BenchmarkHelpers.Keywords(item.Question);
                    var names   = BenchmarkHelpers.PersonNames(item.Question).Select(n => n.ToLowerInvariant()).ToHashSet();
                    var predKws = allKws.Where(w => !names.Contains(w)).ToList();

                    var scored = retrieved
                        .Select((r, ri) => (
                            score: r.Score * (1.0 - 0.5 * BenchmarkHelpers.KeywordOverlap(predKws, retrievedTexts[ri])),
                            id:    r.Id))
                        .OrderByDescending(x => x.score)
                        .Take(topK)
                        .ToList();

                    retrievedIds = scored.Select(x => x.id).ToList();
                }
                else
                {
                    retrievedIds = retrievedIds.Take(topK).ToList();
                }

                // Hit: evidence session IDs in retrieved
                bool hit;
                if (item.EvidenceSessionIds.Count > 0)
                {
                    var targetSet = item.EvidenceSessionIds
                        .Select(eid => $"{itemKey}_{eid}")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    hit = targetSet.Overlaps(retrievedIds);
                }
                else
                {
                    // Fallback: check answer text appears in retrieved snippets
                    hit = retrievedTexts.Any(t =>
                        t.Contains(item.Answer, StringComparison.OrdinalIgnoreCase));
                }

                if (hit) totalHit++;

                results.Add(new
                {
                    question       = item.Question,
                    answer         = item.Answer,
                    retrieved_ids  = retrievedIds,
                    hit_at_k       = hit,
                });

                if ((i + 1) % 50 == 0)
                {
                    double running = totalHit / (double)(i + 1) * 100;
                    Console.WriteLine($"  [{i + 1,4}/{items.Count}]  running R@{topK}: {running:F1}%");
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
        Console.WriteLine($"  RESULTS — MemPalace on LongMemEval ({mode} mode, top-{topK})");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"\n  Overall R@{topK}: {overall:F1}%  ({totalHit}/{items.Count})\n");
        BenchmarkHelpers.Sep('=');

        if (outFile is not null)
        {
            File.WriteAllText(outFile, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Results saved to: {outFile}");
        }
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private sealed record LongMemEvalItem(
        List<(string role, string content)> Turns,
        string                              Question,
        string                              Answer,
        List<int>                           EvidenceSessionIds);

    private static List<LongMemEvalItem> LoadLongMemEval(string filePath, int limit)
    {
        using var stream = File.OpenRead(filePath);
        var root = JsonNode.Parse(stream);
        if (root is not JsonArray arr)
        {
            Console.WriteLine("Expected JSON array at root.");
            return [];
        }

        var items = new List<LongMemEvalItem>();
        foreach (var node in arr)
        {
            if (node is not JsonObject obj) continue;

            var turns = new List<(string role, string content)>();
            if (obj["session"] is JsonArray session)
            {
                foreach (var t in session.OfType<JsonObject>())
                {
                    var role    = t["role"]?.GetValue<string>() ?? "user";
                    var content = t["content"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(content))
                        turns.Add((role, content));
                }
            }

            var question = obj["question"]?.GetValue<string>() ?? "";
            var answer   = obj["answer"]?.GetValue<string>()
                        ?? obj["answers"]?.AsArray().FirstOrDefault()?.GetValue<string>()
                        ?? "";

            var evidenceIds = new List<int>();
            if (obj["evidence_session_ids"] is JsonArray eids)
                foreach (var e in eids)
                    if (e is not null) evidenceIds.Add(e.GetValue<int>());

            if (!string.IsNullOrEmpty(question) && turns.Count > 0)
                items.Add(new LongMemEvalItem(turns, question, answer, evidenceIds));

            if (limit > 0 && items.Count >= limit) break;
        }

        return items;
    }
}
