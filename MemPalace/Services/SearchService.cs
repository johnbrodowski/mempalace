using System.Text.RegularExpressions;

namespace MemPalace.Core;

public sealed class SearchService
{
    // ── Stopwords for keyword deduplication ───────────────────────────────────

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "also", "been", "could", "does", "each", "from", "have",
        "here", "into", "just", "like", "more", "most", "other", "over", "should",
        "some", "such", "than", "that", "their", "them", "then", "there", "these",
        "they", "this", "those", "through", "under", "very", "well", "were", "what",
        "when", "where", "which", "while", "will", "with", "would", "your"
    };

    /// <summary>RRF constant k=60 (Cormack &amp; Clarke 2009). Dampens high-rank dominance.</summary>
    private const int RrfK = 60;

    private readonly AppConfig       _config;
    private readonly DatabaseService _db;
    private readonly EmbeddingService? _embedder;

    public SearchService(AppConfig config, DatabaseService db, EmbeddingService? embedder = null)
    {
        _config   = config;
        _db       = db;
        _embedder = embedder;
    }

    // ── Primary search entry point ────────────────────────────────────────────

    /// <summary>
    /// Search the store. Uses Reciprocal Rank Fusion (RRF) combining BM25 and
    /// cosine-similarity legs when an embedding model is available; falls back
    /// to BM25-only when no model is loaded or the embeddings table is empty.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string  query,
        string? domain = null,
        string? topic  = null,
        int     limit  = 10)
    {
        // Use hybrid only when the model is loaded AND we have embeddings stored
        bool hybridAvailable = _embedder is not null && _db.GetEmbeddingCount() > 0;

        if (!hybridAvailable)
            return _db.SearchFts5(query, domain: domain, topic: topic, limit: limit);

        return await Task.Run(() => HybridSearch(query, domain, topic, limit));
    }

    // ── Hybrid RRF search ─────────────────────────────────────────────────────

    private IReadOnlyList<SearchResult> HybridSearch(
        string query, string? domain, string? topic, int limit)
    {
        int candidateK = limit * 3;   // fetch more candidates for better fusion input

        // ── Leg 1: BM25 (FTS5) ───────────────────────────────────────────────
        var bm25Results = _db.SearchFts5(query, domain, topic, limit: candidateK);

        // ── Leg 2: Cosine similarity ──────────────────────────────────────────
        var queryVec    = _embedder!.Embed(query);
        var allVecs     = _db.GetAllEmbeddings(domain, topic);

        var vectorRanked = allVecs
            .Select(e => (e.ChunkId, Score: EmbeddingService.CosineSimilarity(queryVec, e.Embedding)))
            .OrderByDescending(x => x.Score)
            .Take(candidateK)
            .ToList();

        // ── RRF fusion: score(d) = Σ_r  1 / (k + rank_r(d)) ─────────────────
        var rrfScores = new Dictionary<string, double>(StringComparer.Ordinal);

        for (int rank = 0; rank < bm25Results.Count; rank++)
        {
            var id = bm25Results[rank].Id;
            rrfScores.TryGetValue(id, out var prev);
            rrfScores[id] = prev + 1.0 / (RrfK + rank + 1);
        }

        for (int rank = 0; rank < vectorRanked.Count; rank++)
        {
            var id = vectorRanked[rank].ChunkId;
            rrfScores.TryGetValue(id, out var prev);
            rrfScores[id] = prev + 1.0 / (RrfK + rank + 1);
        }

        // ── Take top-limit by fused score, hydrate full rows ──────────────────
        var topIds = rrfScores
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => kv.Key)
            .ToList();

        var hydrated = _db.GetChunksByIds(topIds);
        var byId     = hydrated.ToDictionary(r => r.Id, StringComparer.Ordinal);

        return topIds
            .Where(id => byId.ContainsKey(id))
            .Select(id =>
            {
                var r = byId[id];
                return new SearchResult
                {
                    Id       = r.Id,
                    Title    = r.Title,
                    Snippet  = r.Snippet,
                    Domain   = r.Domain,
                    Topic    = r.Topic,
                    Category = r.Category,
                    Score    = rrfScores[id],
                };
            })
            .ToList();
    }

    // ── Duplicate detection ───────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="content"/> is likely already in the store.
    /// Uses cosine similarity when the embedding model is available; otherwise
    /// falls back to Jaccard word-overlap on the top BM25 candidates.
    /// </summary>
    public async Task<bool> CheckDuplicateAsync(string content, double threshold = 0.9)
    {
        if (_embedder is not null && _db.GetEmbeddingCount() > 0)
            return await Task.Run(() => CheckDuplicateSemantic(content, threshold));

        return CheckDuplicateKeyword(content, threshold);
    }

    private bool CheckDuplicateSemantic(string content, double threshold)
    {
        var queryVec = _embedder!.Embed(content);
        var allVecs  = _db.GetAllEmbeddings();

        return allVecs.Any(e =>
            EmbeddingService.CosineSimilarity(queryVec, e.Embedding) >= (float)threshold);
    }

    private bool CheckDuplicateKeyword(string content, double threshold)
    {
        var topKeywords = ExtractTopKeywords(content, 5);
        if (topKeywords.Count == 0) return false;

        var results      = _db.SearchFts5(string.Join(" ", topKeywords), limit: 5);
        var contentWords = Tokenize(content);
        if (contentWords.Count == 0) return false;

        return results.Any(r => ComputeJaccard(contentWords, Tokenize(r.Snippet)) >= threshold);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static List<string> ExtractTopKeywords(string content, int topN)
    {
        var words  = Tokenize(content);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in words)
        {
            if (word.Length <= 4 || Stopwords.Contains(word)) continue;
            counts[word] = counts.GetValueOrDefault(word) + 1;
        }

        return counts
            .OrderByDescending(kvp => kvp.Value)
            .Take(topN)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    private static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return Regex.Matches(text.ToLowerInvariant(), @"[a-z]+")
                    .Select(m => m.Value)
                    .ToList();
    }

    private static double ComputeJaccard(IEnumerable<string> a, IEnumerable<string> b)
    {
        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        if (setA.Count == 0 && setB.Count == 0) return 1.0;
        int intersection = setA.Count(word => setB.Contains(word));
        int union        = setA.Count + setB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}
