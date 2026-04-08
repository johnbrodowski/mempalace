namespace MemPalace.Core;

public sealed class PalaceGraphService
{
    private readonly DatabaseService _db;

    public PalaceGraphService(DatabaseService db)
    {
        _db = db;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build a graph of topics (nodes) connected via shared domains (edges).
    /// Each node tracks which domains and categories that topic appears in, plus chunk count.
    /// Each edge represents a pair of topics that share a domain.
    /// </summary>
    public (Dictionary<string, TopicNode> nodes, IReadOnlyList<GraphEdge> edges) BuildGraph()
    {
        var chunks = _db.GetAllChunks();

        // Group chunks by topic name to build nodes
        var nodeMap = new Dictionary<string, (HashSet<string> Domains, HashSet<string> Categories, int Count)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in chunks)
        {
            if (!nodeMap.TryGetValue(chunk.Topic, out var data))
            {
                data = (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        0);
                nodeMap[chunk.Topic] = data;
            }
            if (!string.IsNullOrEmpty(chunk.Domain))
                data.Domains.Add(chunk.Domain);
            if (!string.IsNullOrEmpty(chunk.Category))
                data.Categories.Add(chunk.Category);
            nodeMap[chunk.Topic] = (data.Domains, data.Categories, data.Count + 1);
        }

        var nodes = nodeMap.ToDictionary(
            kv => kv.Key,
            kv => new TopicNode
            {
                Topic      = kv.Key,
                Domains    = kv.Value.Domains,
                Categories = kv.Value.Categories,
                ChunkCount = kv.Value.Count,
            },
            StringComparer.OrdinalIgnoreCase);

        // Build edges: for each pair of topics sharing a domain, add a GraphEdge
        // Index topics by domain first for efficiency
        var domainToTopics = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (topic, node) in nodes)
        {
            foreach (var domain in node.Domains)
            {
                if (!domainToTopics.TryGetValue(domain, out var topicList))
                {
                    topicList = new List<string>();
                    domainToTopics[domain] = topicList;
                }
                topicList.Add(topic);
            }
        }

        var edgeSet = new HashSet<string>(StringComparer.Ordinal);
        var edges   = new List<GraphEdge>();

        foreach (var (domain, topics) in domainToTopics)
        {
            for (int i = 0; i < topics.Count; i++)
            {
                for (int j = i + 1; j < topics.Count; j++)
                {
                    // Canonical key so (A,B) and (B,A) are not both added
                    string a   = string.Compare(topics[i], topics[j], StringComparison.OrdinalIgnoreCase) <= 0
                                     ? topics[i] : topics[j];
                    string b   = a == topics[i] ? topics[j] : topics[i];
                    string key = $"{a}|{b}|{domain}";

                    if (edgeSet.Add(key))
                    {
                        edges.Add(new GraphEdge { From = a, To = b, Via = domain });
                    }
                }
            }
        }

        return (nodes, edges.AsReadOnly());
    }

    /// <summary>
    /// BFS traversal starting from <paramref name="startTopic"/>, following edges
    /// that share a domain, up to <paramref name="maxHops"/> hops.
    /// Returns at most 50 results.
    /// </summary>
    public IReadOnlyList<TraversalResult> Traverse(string startTopic, int maxHops = 2)
    {
        var (nodes, edges) = BuildGraph();

        if (!nodes.ContainsKey(startTopic))
            return [];

        // Adjacency: topic → list of (neighbour, via-domain)
        var adj = BuildAdjacency(edges);

        var results  = new List<TraversalResult>();
        var visited  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startTopic };
        var queue    = new Queue<(string Topic, string Domain, int Hops)>();

        // Seed queue with direct neighbours of the start topic
        if (adj.TryGetValue(startTopic, out var startNeighbours))
        {
            foreach (var (neighbour, domain) in startNeighbours)
            {
                if (visited.Add(neighbour))
                    queue.Enqueue((neighbour, domain, 1));
            }
        }

        while (queue.Count > 0 && results.Count < 50)
        {
            var (topic, domain, hops) = queue.Dequeue();
            results.Add(new TraversalResult { Topic = topic, Domain = domain, Hops = hops });

            if (hops >= maxHops) continue;

            if (!adj.TryGetValue(topic, out var neighbours)) continue;

            foreach (var (neighbour, nextDomain) in neighbours)
            {
                if (visited.Add(neighbour))
                    queue.Enqueue((neighbour, nextDomain, hops + 1));
            }
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Find "link" topics — topics that appear in 2 or more domains.
    /// Optionally filter by one or both domain names.
    /// </summary>
    public IReadOnlyList<LinkResult> FindLinks(string? domainA = null, string? domainB = null)
    {
        var (nodes, _) = BuildGraph();

        var results = new List<LinkResult>();

        foreach (var (_, node) in nodes)
        {
            bool isLink;

            if (domainA is not null && domainB is not null)
            {
                // Topic must appear in BOTH specified domains
                isLink = node.Domains.Contains(domainA) && node.Domains.Contains(domainB);
            }
            else if (domainA is not null)
            {
                // Topic must be in domainA AND at least one other domain
                isLink = node.Domains.Contains(domainA) && node.Domains.Count >= 2;
            }
            else
            {
                // Any topic in 2+ domains
                isLink = node.Domains.Count >= 2;
            }

            if (isLink)
            {
                results.Add(new LinkResult
                {
                    Topic            = node.Topic,
                    ConnectedDomains = node.Domains.OrderBy(d => d).ToList().AsReadOnly(),
                });
            }
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Return summary statistics for the store graph.
    /// </summary>
    public GraphStats GetStats()
    {
        var (nodes, edges) = BuildGraph();

        var linkCount   = nodes.Values.Count(n => n.Domains.Count >= 2);
        var domainCount = nodes.Values.SelectMany(n => n.Domains)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .Count();

        return new GraphStats
        {
            NodeCount   = nodes.Count,
            EdgeCount   = edges.Count,
            LinkCount   = linkCount,
            DomainCount = domainCount,
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Dictionary<string, List<(string Neighbour, string Domain)>> BuildAdjacency(
        IReadOnlyList<GraphEdge> edges)
    {
        var adj = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            if (!adj.TryGetValue(edge.From, out var fromList))
            {
                fromList = new List<(string, string)>();
                adj[edge.From] = fromList;
            }
            fromList.Add((edge.To, edge.Via));

            if (!adj.TryGetValue(edge.To, out var toList))
            {
                toList = new List<(string, string)>();
                adj[edge.To] = toList;
            }
            toList.Add((edge.From, edge.Via));
        }

        return adj;
    }
}
