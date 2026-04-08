using System.Text.Json;
using System.Text.Json.Nodes;
using MemPalace.Core;

namespace MemPalace.Benchmarks;

/// <summary>
/// MemPalace × LoCoMo Benchmark
/// ==============================
/// Mirrors Python benchmarks/locomo_bench.py (core retrieval modes only).
///
/// LoCoMo: Long-Context Memory benchmark testing retrieval over long dialogues.
/// Data format: JSON with sessions list, each session has "dialogue" turns and
/// a "qa" list with {question, answers, evidence_utterance_ids}.
///
/// Usage:
///   mempalace-bench locomo &lt;data_file.json&gt; [--top-k N] [--mode raw|hybrid]
/// </summary>
public static class LoCoMoRunner
{
    public static void Run(
        string dataFile,
        int    topK  = 5,
        string mode  = "hybrid",
        string? outFile = null)
    {
        if (!File.Exists(dataFile))
        {
            Console.WriteLine($"File not found: {dataFile}");
            return;
        }

        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine("  MemPalace × LoCoMo (C#)");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"  Data file: {dataFile}");
        Console.WriteLine($"  Top-k:     {topK}");
        Console.WriteLine($"  Mode:      {mode}");
        BenchmarkHelpers.Sep();
        Console.WriteLine();

        var data = LoadLoCoMo(dataFile);
        if (data.Count == 0)
        {
            Console.WriteLine("No sessions found.");
            return;
        }

        int totalHit = 0, totalQA = 0;
        var results = new List<object>();

        foreach (var session in data)
        {
            var (db, tempDir) = BenchmarkHelpers.MakeTempDb();
            try
            {
                // Index all dialogue turns for this session
                int gi = 0;
                var docs = session.Turns.Select(t => (
                    id:     $"{session.Id}_{gi++}",
                    text:   t.Text,
                    domain: session.Id,
                    topic:  "turn"));
                BenchmarkHelpers.IndexDocuments(db, docs);

                // Evaluate each QA pair
                foreach (var qa in session.QAs)
                {
                    int nRetrieve = mode == "hybrid" ? topK * 3 : topK;
                    var retrieved = db.SearchFts5(qa.Question, limit: nRetrieve);
                    var retrievedIds = retrieved.Select(r => r.Id).ToList();
                    var retrievedTexts = retrieved.Select(r => r.Snippet).ToList();

                    if (mode == "hybrid")
                    {
                        var allKws  = BenchmarkHelpers.Keywords(qa.Question);
                        var names   = BenchmarkHelpers.PersonNames(qa.Question).Select(n => n.ToLowerInvariant()).ToHashSet();
                        var predKws = allKws.Where(w => !names.Contains(w)).ToList();

                        var scored = retrieved
                            .Select((r, i) => (
                                score: r.Score * (1.0 - 0.5 * BenchmarkHelpers.KeywordOverlap(predKws, retrievedTexts[i])),
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

                    // Hit: any evidence utterance ID appears among retrieved turn IDs
                    var targetSet = qa.EvidenceUtteranceIds
                        .Select(uid => $"{session.Id}_{uid}")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    bool hit = targetSet.Overlaps(retrievedIds);
                    if (hit) totalHit++;
                    totalQA++;

                    results.Add(new
                    {
                        session_id   = session.Id,
                        question     = qa.Question,
                        answers      = qa.Answers,
                        target_ids   = targetSet.ToList(),
                        retrieved_ids = retrievedIds,
                        hit_at_k     = hit,
                    });
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        double overall = totalQA > 0 ? totalHit / (double)totalQA * 100 : 0;
        Console.WriteLine();
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"  RESULTS — MemPalace on LoCoMo ({mode} mode, top-{topK})");
        BenchmarkHelpers.Sep('=');
        Console.WriteLine($"\n  Overall R@{topK}: {overall:F1}%  ({totalHit}/{totalQA})\n");
        BenchmarkHelpers.Sep('=');

        if (outFile is not null)
        {
            File.WriteAllText(outFile, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Results saved to: {outFile}");
        }
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private sealed record LoCoMoTurn(int Idx, string Text);
    private sealed record LoCoMoQA(string Question, List<string> Answers, List<int> EvidenceUtteranceIds);
    private sealed record LoCoMoSession(string Id, List<LoCoMoTurn> Turns, List<LoCoMoQA> QAs);

    private static List<LoCoMoSession> LoadLoCoMo(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var root = JsonNode.Parse(stream);
        var sessions = new List<LoCoMoSession>();

        // Support array-of-sessions or dict keyed by session id
        IEnumerable<(string id, JsonNode? node)> entries = root switch
        {
            JsonArray arr  => arr.Select((n, i) => ($"session_{i}", n)),
            JsonObject obj => obj.Select(kv => (kv.Key, (JsonNode?)kv.Value)),
            _              => []
        };

        foreach (var (sid, node) in entries)
        {
            if (node is not JsonObject obj) continue;

            // Parse dialogue turns
            var turns = new List<LoCoMoTurn>();
            if (obj["dialogue"] is JsonArray dialogue)
            {
                int idx = 0;
                foreach (var t in dialogue.OfType<JsonObject>())
                {
                    var text = t["text"]?.GetValue<string>()
                            ?? t["utterance"]?.GetValue<string>()
                            ?? t["content"]?.GetValue<string>()
                            ?? "";
                    var speaker = t["speaker"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(text))
                        turns.Add(new LoCoMoTurn(idx, $"[{speaker}] {text}".Trim()));
                    idx++;
                }
            }

            // Parse QA pairs
            var qas = new List<LoCoMoQA>();
            if (obj["qa"] is JsonArray qaArr)
            {
                foreach (var q in qaArr.OfType<JsonObject>())
                {
                    var question = q["question"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(question)) continue;

                    var answers = q["answers"] is JsonArray aans
                        ? aans.Select(a => a?.GetValue<string>() ?? "").ToList()
                        : [q["answer"]?.GetValue<string>() ?? ""];

                    var evidenceIds = new List<int>();
                    if (q["evidence_utterance_ids"] is JsonArray eids)
                        foreach (var e in eids)
                            if (e is not null && e.GetValue<int>() is int eid)
                                evidenceIds.Add(eid);

                    qas.Add(new LoCoMoQA(question, answers, evidenceIds));
                }
            }

            if (turns.Count > 0 && qas.Count > 0)
                sessions.Add(new LoCoMoSession(sid, turns, qas));
        }

        return sessions;
    }
}
