using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MemPalace.Core;

namespace MemPalace.Benchmarks;

/// <summary>
/// Shared utilities used by all benchmark runners.
/// Mirrors stop-word lists and keyword helpers from membench_bench.py and locomo_bench.py.
/// </summary>
internal static class BenchmarkHelpers
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what","when","where","who","how","which","did","do","was","were","have","has","had",
        "is","are","the","a","an","my","me","i","you","your","their","it","its","in","on",
        "at","to","for","of","with","by","from","ago","last","that","this","there","about",
        "get","got","give","gave","buy","bought","made","make","said","would","could","should",
        "might","can","will","shall","kind","type","like","prefer","enjoy","think","feel"
    };

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "What","When","Where","Who","How","Which","Did","Do","Was","Were","Have","Has","Had",
        "Is","Are","The","My","Our","I","It","Its","This","That","These","Those"
    };

    /// <summary>Extract meaningful keywords from text (excluding stop words).</summary>
    public static List<string> Keywords(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"\b[a-z]{3,}\b");
        return words.Select(m => m.Value).Where(w => !StopWords.Contains(w)).ToList();
    }

    /// <summary>Fraction of query keywords that appear in doc.</summary>
    public static double KeywordOverlap(IReadOnlyList<string> queryKws, string docText)
    {
        if (queryKws.Count == 0) return 0.0;
        var lower = docText.ToLowerInvariant();
        return queryKws.Count(kw => lower.Contains(kw)) / (double)queryKws.Count;
    }

    /// <summary>Capitalised words that are likely proper names.</summary>
    public static List<string> PersonNames(string text)
        => Regex.Matches(text, @"\b[A-Z][a-z]{2,15}\b")
               .Select(m => m.Value)
               .Where(w => !NotNames.Contains(w))
               .Distinct()
               .ToList();

    // ── Ephemeral per-run DatabaseService ─────────────────────────────────────

    /// <summary>
    /// Create a temporary DatabaseService backed by a fresh SQLite file.
    /// Caller is responsible for deleting the temp dir when done.
    /// </summary>
    public static (DatabaseService db, string tempDir) MakeTempDb()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mempalace_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var cfg = new AppConfig
        {
            DbPath    = Path.Combine(tempDir, "bench.db"),
            StorePath = tempDir,
            IndexPath = tempDir,
        };
        return (new DatabaseService(cfg), tempDir);
    }

    /// <summary>Stable chunk ID from content (mirrors Python md5 approach).</summary>
    public static string ChunkId(string content, int idx)
    {
        var raw = $"{content}{idx}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Index a list of text documents as chunks under a given domain.</summary>
    public static int IndexDocuments(
        DatabaseService db,
        IEnumerable<(string id, string text, string domain, string topic)> docs)
    {
        int count = 0;
        foreach (var (id, text, domain, topic) in docs)
        {
            db.AddChunk(new ChunkRecord
            {
                Id         = id,
                Domain     = domain,
                Topic      = topic,
                Category   = "",
                SourceFile = id,   // store doc id as SourceFile so we can retrieve it
                Content    = text,
                ChunkIndex = 0,
                AddedBy    = "bench",
                FiledAt    = DateTime.UtcNow.ToString("o"),
            });
            count++;
        }
        return count;
    }

    /// <summary>Print a separator line.</summary>
    public static void Sep(char c = '─', int width = 58)
        => Console.WriteLine(new string(c, width));
}
