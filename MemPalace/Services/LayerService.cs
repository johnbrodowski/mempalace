namespace MemPalace.Core;

/// <summary>
/// Four-layer memory stack for MemPalace context assembly.
///   L0 – static identity file
///   L1 – recent chunks (essential story)
///   L2 – on-demand retrieval filtered by domain/topic
///   L3 – deep FTS5 search
/// </summary>
public sealed class LayerService
{
    private const int MaxChunks = 15;
    private const int MaxChars  = 3200;

    private readonly AppConfig            _config;
    private readonly DatabaseService      _db;
    private readonly KnowledgeGraphService _kg;

    public LayerService(AppConfig config, DatabaseService db, KnowledgeGraphService kg)
    {
        _config = config;
        _db     = db;
        _kg     = kg;
    }

    // -------------------------------------------------------------------------
    // L0 – Identity
    // -------------------------------------------------------------------------

    /// <summary>
    /// Return the user's static identity file, prefixed with an L0 header.
    /// </summary>
    public string RenderLayer0()
    {
        const string header = "[L0 IDENTITY]\n";

        try
        {
            if (File.Exists(_config.IdentityPath))
                return header + File.ReadAllText(_config.IdentityPath);
        }
        catch
        {
            // Fall through to the placeholder below
        }

        return header + "(No identity.txt found. Create ~/.mempalace/identity.txt to define yourself.)";
    }

    // -------------------------------------------------------------------------
    // L1 – Essential Story
    // -------------------------------------------------------------------------

    /// <summary>
    /// Render the most recently filed chunks grouped by topic.
    /// Groups are sorted by size (desc) and each contributes at most 2 × 200-char snippets.
    /// Output is hard-capped at <see cref="MaxChars"/> characters.
    /// </summary>
    public string GenerateLayer1(string? domain = null)
    {
        const string header = "[L1 ESSENTIAL STORY]\n";

        var chunks = _db.GetRecentChunks(MaxChunks, domain);

        if (chunks.Count == 0)
            return header + "(Store is empty. Run 'mempalace mine' to index your files.)";

        // Group by topic, sorted by group size descending
        var grouped = chunks
            .GroupBy(d => d.Topic, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var sb = new System.Text.StringBuilder(header);
        int totalChars = header.Length;

        foreach (var group in grouped)
        {
            if (totalChars >= MaxChars) break;

            string topicHeader = $"\n## {group.Key}\n";
            sb.Append(topicHeader);
            totalChars += topicHeader.Length;

            int snippetCount = 0;
            foreach (var chunk in group)
            {
                if (snippetCount >= 2 || totalChars >= MaxChars) break;

                string snippet = chunk.Content.Length > 200
                    ? chunk.Content[..200] + "…"
                    : chunk.Content;
                string line = snippet + "\n";
                sb.Append(line);
                totalChars += line.Length;
                snippetCount++;
            }
        }

        // Hard cap
        string result = sb.ToString();
        return result.Length > MaxChars ? result[..MaxChars] : result;
    }

    // -------------------------------------------------------------------------
    // L2 – On-Demand Retrieval
    // -------------------------------------------------------------------------

    /// <summary>
    /// Return up to <paramref name="nResults"/> chunks filtered by domain/topic.
    /// </summary>
    public string RetrieveLayer2(string? domain = null, string? topic = null, int nResults = 10)
    {
        string domainLabel = domain ?? "*";
        string topicLabel  = topic  ?? "*";
        string header      = $"[L2 ON-DEMAND: {domainLabel}/{topicLabel}]\n";

        var chunks = _db.GetAllChunks(domain, topic).Take(nResults).ToList();

        if (chunks.Count == 0)
            return header + $"(No chunks found for domain={domainLabel}, topic={topicLabel}.)";

        var sb = new System.Text.StringBuilder(header);
        foreach (var chunk in chunks)
        {
            string snippet = chunk.Content.Length > 200
                ? chunk.Content[..200] + "…"
                : chunk.Content;
            sb.AppendLine(snippet);
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // L3 – Deep FTS5 Search
    // -------------------------------------------------------------------------

    /// <summary>
    /// Full-text search via FTS5, optionally scoped to a domain/topic.
    /// </summary>
    public Task<string> SearchLayer3(string query, string? domain = null, string? topic = null, int nResults = 5)
    {
        string header = $"[L3 DEEP SEARCH: {query}]\n";

        var results = _db.SearchFts5(query, domain, topic, nResults);

        if (results.Count == 0)
        {
            string empty = header + $"(No results found for query: {query})";
            return Task.FromResult(empty);
        }

        var sb = new System.Text.StringBuilder(header);
        foreach (var result in results)
        {
            string location = $"[{result.Domain}/{result.Topic}]";
            string snippet  = result.Snippet.Length > 200
                ? result.Snippet[..200] + "…"
                : result.Snippet;
            sb.AppendLine($"{location} {snippet}");
        }

        return Task.FromResult(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Wake-up context assembly
    // -------------------------------------------------------------------------

    /// <summary>
    /// Combine L0 + L1, and L2 (if a domain is specified), into a single wake-up
    /// context string separated by horizontal rules.
    /// </summary>
    public Task<string> WakeUpAsync(string? domain = null)
    {
        var layers = new List<string>
        {
            RenderLayer0(),
            GenerateLayer1(domain),
        };

        if (domain is not null)
            layers.Add(RetrieveLayer2(domain));

        string combined = string.Join("\n\n---\n\n", layers);
        return Task.FromResult(combined);
    }

    // -------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------

    /// <summary>
    /// Return a status dictionary suitable for display or serialisation.
    /// </summary>
    public Task<Dictionary<string, object>> StatusAsync()
    {
        var kgStats = _kg.Stats();

        var status = new Dictionary<string, object>
        {
            ["version"]      = _config.Version,
            ["chunk_count"]  = _db.GetChunkCount(),
            ["domain_count"] = _db.GetDomains().Count,
            ["topic_count"]  = _db.GetTopics().Count,
            ["has_identity"] = File.Exists(_config.IdentityPath),
            ["kg_stats"]     = kgStats,
        };

        return Task.FromResult(status);
    }
}
