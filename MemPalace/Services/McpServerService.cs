using System.Text.Json;
using System.Text.Json.Nodes;

namespace MemPalace.Core;

public sealed class McpServerService
{
    private const string PalaceProtocol = """
        ## Memory Protocol
        1. VERIFY before speaking - check store before answering memory questions
        2. FILE important info - use mempalace_add_chunk to save key facts
        3. CONNECT knowledge - use kg_add to link entities and facts
        4. NAVIGATE context - use traverse and find_links to explore
        5. MAINTAIN accuracy - use kg_invalidate when facts change
        """;

    private static readonly JsonSerializerOptions SerOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly AppConfig _config;
    private readonly DatabaseService _db;
    private readonly KnowledgeGraphService _kg;
    private readonly SearchService _searcher;
    private readonly PalaceGraphService _graph;
    private readonly LayerService _layers;
    private readonly DialectService _dialect;

    public McpServerService(
        AppConfig config,
        DatabaseService db,
        KnowledgeGraphService kg,
        SearchService searcher,
        PalaceGraphService graph,
        LayerService layers,
        DialectService? dialect = null)
    {
        _config  = config;
        _db      = db;
        _kg      = kg;
        _searcher = searcher;
        _graph   = graph;
        _layers  = layers;
        _dialect = dialect ?? new DialectService();
    }

    // -----------------------------------------------------------------------
    // Main loop
    // -----------------------------------------------------------------------

    public async Task RunAsync(CancellationToken ct = default)
    {
        var stdin = Console.In;
        var stdout = Console.Out;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null) break; // EOF

            line = line.Trim();
            if (line.Length == 0) continue;

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch
            {
                var errResponse = MakeError(null, -32700, "Parse error");
                await stdout.WriteLineAsync(errResponse.ToJsonString());
                await stdout.FlushAsync(ct);
                continue;
            }

            JsonNode? response;
            try
            {
                response = await HandleRequestAsync(request!);
            }
            catch (Exception ex)
            {
                var id = request?["id"];
                response = MakeError(id, -32603, ex.Message);
            }

            if (response is not null)
            {
                await stdout.WriteLineAsync(response.ToJsonString());
                await stdout.FlushAsync(ct);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Request dispatch
    // -----------------------------------------------------------------------

    private async Task<JsonNode?> HandleRequestAsync(JsonNode request)
    {
        var method = request["method"]?.GetValue<string>() ?? string.Empty;
        var id = request["id"];

        // Notifications have no id — no response expected
        if (id is null && !method.StartsWith("initialize", StringComparison.Ordinal))
            return null;

        return method switch
        {
            "initialize"  => HandleInitialize(id),
            "tools/list"  => HandleToolsList(id),
            "tools/call"  => await HandleToolsCallAsync(
                                 id,
                                 request["params"]?["name"]?.GetValue<string>() ?? string.Empty,
                                 request["params"]?["arguments"]),
            _ => id is not null
                     ? MakeError(id, -32601, $"Method not found: {method}")
                     : null
        };
    }

    // -----------------------------------------------------------------------
    // Protocol handlers
    // -----------------------------------------------------------------------

    private JsonNode HandleInitialize(object? id)
    {
        var result = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"]    = "mempalace",
                ["version"] = _config.Version
            }
        };
        return MakeResult(id, result);
    }

    private JsonNode HandleToolsList(object? id)
    {
        var tools = new JsonArray
        {
            MakeTool("mempalace_status",
                "Get store overview and current statistics.",
                new JsonObject()),

            MakeTool("mempalace_list_domains",
                "List all domains in the memory store.",
                new JsonObject()),

            MakeTool("mempalace_list_topics",
                "List topics, optionally filtered by domain.",
                new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["domain"] = new JsonObject { ["type"] = "string", ["description"] = "Domain name filter" }
                    }
                }),

            MakeTool("mempalace_search",
                "Search the store using full-text search.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "query" },
                    ["properties"] = new JsonObject
                    {
                        ["query"]  = new JsonObject { ["type"] = "string", ["description"] = "Search query" },
                        ["domain"] = new JsonObject { ["type"] = "string" },
                        ["topic"]  = new JsonObject { ["type"] = "string" },
                        ["limit"]  = new JsonObject { ["type"] = "integer", ["default"] = 5 }
                    }
                }),

            MakeTool("mempalace_check_duplicate",
                "Check if content already exists in the store.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "content" },
                    ["properties"] = new JsonObject
                    {
                        ["content"]   = new JsonObject { ["type"] = "string" },
                        ["threshold"] = new JsonObject { ["type"] = "number", ["default"] = 0.9 }
                    }
                }),

            MakeTool("mempalace_add_chunk",
                "Add a new chunk (memory entry) to the store.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "domain", "topic", "content" },
                    ["properties"] = new JsonObject
                    {
                        ["domain"]      = new JsonObject { ["type"] = "string" },
                        ["topic"]       = new JsonObject { ["type"] = "string" },
                        ["content"]     = new JsonObject { ["type"] = "string" },
                        ["source_file"] = new JsonObject { ["type"] = "string" },
                        ["added_by"]    = new JsonObject { ["type"] = "string" }
                    }
                }),

            MakeTool("mempalace_delete_chunk",
                "Delete a chunk by its ID.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "chunk_id" },
                    ["properties"] = new JsonObject
                    {
                        ["chunk_id"] = new JsonObject { ["type"] = "string" }
                    }
                }),

            MakeTool("mempalace_kg_query",
                "Query the knowledge graph for an entity.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "entity" },
                    ["properties"] = new JsonObject
                    {
                        ["entity"]    = new JsonObject { ["type"] = "string" },
                        ["as_of"]     = new JsonObject { ["type"] = "string" },
                        ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "both", "subject", "object" } }
                    }
                }),

            MakeTool("mempalace_kg_add",
                "Add a triple (fact) to the knowledge graph.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "subject", "predicate", "object" },
                    ["properties"] = new JsonObject
                    {
                        ["subject"]        = new JsonObject { ["type"] = "string" },
                        ["predicate"]      = new JsonObject { ["type"] = "string" },
                        ["object"]         = new JsonObject { ["type"] = "string" },
                        ["valid_from"]     = new JsonObject { ["type"] = "string" },
                        ["source_closet"]  = new JsonObject { ["type"] = "string" }
                    }
                }),

            MakeTool("mempalace_kg_invalidate",
                "Mark a knowledge graph triple as no longer valid.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "subject", "predicate", "object" },
                    ["properties"] = new JsonObject
                    {
                        ["subject"]   = new JsonObject { ["type"] = "string" },
                        ["predicate"] = new JsonObject { ["type"] = "string" },
                        ["object"]    = new JsonObject { ["type"] = "string" },
                        ["ended"]     = new JsonObject { ["type"] = "string" }
                    }
                }),

            MakeTool("mempalace_kg_timeline",
                "Get a timeline of knowledge graph facts, optionally for a specific entity.",
                new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["entity"] = new JsonObject { ["type"] = "string" }
                    }
                }),

            MakeTool("mempalace_kg_stats",
                "Get knowledge graph statistics.",
                new JsonObject()),

            MakeTool("mempalace_traverse",
                "Traverse the store graph from a starting topic.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "start_room" },
                    ["properties"] = new JsonObject
                    {
                        ["start_room"] = new JsonObject { ["type"] = "string" },
                        ["max_hops"]   = new JsonObject { ["type"] = "integer", ["default"] = 2 }
                    }
                }),

            MakeTool("mempalace_find_links",
                "Find topics that connect multiple domains.",
                new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["domain_a"] = new JsonObject { ["type"] = "string" },
                        ["domain_b"] = new JsonObject { ["type"] = "string" }
                    }
                }),

            MakeTool("mempalace_log_write",
                "Write a log entry for an agent.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "agent_name", "entry", "topic" },
                    ["properties"] = new JsonObject
                    {
                        ["agent_name"] = new JsonObject { ["type"] = "string" },
                        ["entry"]      = new JsonObject { ["type"] = "string" },
                        ["topic"]      = new JsonObject { ["type"] = "string" }
                    }
                }),

            MakeTool("mempalace_log_read",
                "Read log entries for an agent.",
                new JsonObject
                {
                    ["required"] = new JsonArray { "agent_name" },
                    ["properties"] = new JsonObject
                    {
                        ["agent_name"] = new JsonObject { ["type"] = "string" },
                        ["last_n"]     = new JsonObject { ["type"] = "integer", ["default"] = 10 }
                    }
                }),

            MakeTool("mempalace_get_aaak_spec",
                "Returns the full AAAK compression dialect specification: emotion codes, flag signals, output format, and compression steps.",
                new JsonObject()),

            MakeTool("mempalace_graph_stats",
                "Returns palace graph statistics: topic node count, edge count, cross-domain link count, and domain count.",
                new JsonObject())
        };

        return MakeResult(id, new JsonObject { ["tools"] = tools });
    }

    private static JsonObject MakeTool(string name, string description, JsonObject inputSchemaProps)
    {
        var schema = new JsonObject
        {
            ["type"] = "object"
        };

        // Merge properties and required from inputSchemaProps
        if (inputSchemaProps["properties"] is JsonObject props)
            schema["properties"] = props.DeepClone();
        if (inputSchemaProps["required"] is JsonArray req)
            schema["required"] = req.DeepClone();

        return new JsonObject
        {
            ["name"]        = name,
            ["description"] = description,
            ["inputSchema"] = schema
        };
    }

    private async Task<JsonNode> HandleToolsCallAsync(object? id, string toolName, JsonNode? args)
    {
        try
        {
            var result = toolName switch
            {
                "mempalace_status"          => ToolStatus(),
                "mempalace_list_domains"    => ToolListDomains(),
                "mempalace_list_topics"     => ToolListTopics(args),
                "mempalace_search"          => await ToolSearch(args),
                "mempalace_check_duplicate" => await ToolCheckDuplicate(args),
                "mempalace_add_chunk"       => ToolAddChunk(args),
                "mempalace_delete_chunk"    => ToolDeleteChunk(args),
                "mempalace_kg_query"        => ToolKgQuery(args),
                "mempalace_kg_add"          => ToolKgAdd(args),
                "mempalace_kg_invalidate"   => ToolKgInvalidate(args),
                "mempalace_kg_timeline"     => ToolKgTimeline(args),
                "mempalace_kg_stats"        => ToolKgStats(),
                "mempalace_traverse"        => ToolTraverse(args),
                "mempalace_find_links"      => ToolFindLinks(args),
                "mempalace_log_write"       => ToolLogWrite(args),
                "mempalace_log_read"        => ToolLogRead(args),
                "mempalace_get_aaak_spec"   => ToolGetAaakSpec(),
                "mempalace_graph_stats"     => ToolGraphStats(),
                _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
            };

            return MakeResult(id, result);
        }
        catch (Exception ex)
        {
            return MakeError(id, -32603, ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // JSONRPC helpers
    // -----------------------------------------------------------------------

    private static JsonNode MakeResult(object? id, object result)
    {
        var node = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id is null ? JsonValue.Create((string?)null) : JsonValue.Create(id.ToString())
        };

        node["result"] = result switch
        {
            JsonNode jn => jn,
            string s    => JsonValue.Create(s),
            _           => JsonNode.Parse(JsonSerializer.Serialize(result, SerOpts))
        };

        return node;
    }

    private static JsonNode MakeError(object? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id is null ? JsonValue.Create((string?)null) : JsonValue.Create(id.ToString()),
            ["error"]   = new JsonObject
            {
                ["code"]    = code,
                ["message"] = message
            }
        };
    }

    /// <summary>Wrap a tool result value as MCP content array.</summary>
    private static JsonNode ToolContent(object payload)
    {
        var text = payload switch
        {
            string s => s,
            _        => JsonSerializer.Serialize(payload, SerOpts)
        };

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            }
        };
    }

    // -----------------------------------------------------------------------
    // Tool implementations
    // -----------------------------------------------------------------------

    private JsonNode ToolStatus()
    {
        var status = _layers.StatusAsync().GetAwaiter().GetResult();
        status["palace_protocol"] = PalaceProtocol;
        return ToolContent(status);
    }

    private JsonNode ToolListDomains()
    {
        var domains = _db.GetDomains();
        return ToolContent(new { domains, count = domains.Count });
    }

    private JsonNode ToolListTopics(JsonNode? args)
    {
        var domain = args?["domain"]?.GetValue<string>();
        var topics = _db.GetTopics(domain);
        return ToolContent(new { topics, domain = domain ?? "all", count = topics.Count });
    }

    private async Task<JsonNode> ToolSearch(JsonNode? args)
    {
        var query  = args?["query"]?.GetValue<string>()
                     ?? throw new ArgumentException("query is required");
        var domain = args?["domain"]?.GetValue<string>();
        var topic  = args?["topic"]?.GetValue<string>();
        var limit  = args?["limit"]?.GetValue<int>() ?? 5;

        var results = await _searcher.SearchAsync(query, domain, topic, limit);

        var items = results.Select(r => new
        {
            id       = r.Id,
            domain   = r.Domain,
            topic    = r.Topic,
            category = r.Category,
            snippet  = r.Snippet,
            score    = r.Score
        }).ToList();

        return ToolContent(new { query, count = items.Count, results = items });
    }

    private async Task<JsonNode> ToolCheckDuplicate(JsonNode? args)
    {
        var content   = args?["content"]?.GetValue<string>()
                        ?? throw new ArgumentException("content is required");
        var threshold = args?["threshold"]?.GetValue<double>() ?? 0.9;

        var isDuplicate = await _searcher.CheckDuplicateAsync(content, threshold);
        return ToolContent(new { is_duplicate = isDuplicate, threshold });
    }

    private JsonNode ToolAddChunk(JsonNode? args)
    {
        var domain     = args?["domain"]?.GetValue<string>()  ?? throw new ArgumentException("domain is required");
        var topic      = args?["topic"]?.GetValue<string>()   ?? throw new ArgumentException("topic is required");
        var content    = args?["content"]?.GetValue<string>() ?? throw new ArgumentException("content is required");
        var sourceFile = args?["source_file"]?.GetValue<string>() ?? string.Empty;
        var addedBy    = args?["added_by"]?.GetValue<string>()    ?? "mcp";

        var doc = new ChunkRecord
        {
            Id         = Guid.NewGuid().ToString(),
            Domain     = domain,
            Topic      = topic,
            Content    = content,
            SourceFile = sourceFile,
            AddedBy    = addedBy
        };

        _db.AddChunk(doc);
        return ToolContent(new { success = true, chunk_id = doc.Id, domain, topic });
    }

    private JsonNode ToolDeleteChunk(JsonNode? args)
    {
        var chunkId = args?["chunk_id"]?.GetValue<string>()
                      ?? throw new ArgumentException("chunk_id is required");

        _db.DeleteChunk(chunkId);
        return ToolContent(new { success = true, chunk_id = chunkId });
    }

    private JsonNode ToolKgQuery(JsonNode? args)
    {
        var entity    = args?["entity"]?.GetValue<string>()    ?? throw new ArgumentException("entity is required");
        var asOf      = args?["as_of"]?.GetValue<string>();
        var direction = args?["direction"]?.GetValue<string>() ?? "both";

        var facts = _kg.QueryEntity(entity, asOf, direction);

        var items = facts.Select(f => new
        {
            subject   = f.Subject,
            predicate = f.Predicate,
            @object   = f.Object,
            valid_from = f.ValidFrom,
            valid_to   = f.ValidTo,
            confidence = f.Confidence
        }).ToList();

        return ToolContent(new { entity, direction, count = items.Count, facts = items });
    }

    private JsonNode ToolKgAdd(JsonNode? args)
    {
        var subject       = args?["subject"]?.GetValue<string>()   ?? throw new ArgumentException("subject is required");
        var predicate     = args?["predicate"]?.GetValue<string>()  ?? throw new ArgumentException("predicate is required");
        var obj           = args?["object"]?.GetValue<string>()     ?? throw new ArgumentException("object is required");
        var validFrom     = args?["valid_from"]?.GetValue<string>();
        var sourceRef     = args?["source_ref"]?.GetValue<string>();

        var tripleId = _kg.AddTriple(subject, predicate, obj, validFrom: validFrom, sourceRef: sourceRef);
        return ToolContent(new { success = true, triple_id = tripleId, subject, predicate, @object = obj });
    }

    private JsonNode ToolKgInvalidate(JsonNode? args)
    {
        var subject   = args?["subject"]?.GetValue<string>()   ?? throw new ArgumentException("subject is required");
        var predicate = args?["predicate"]?.GetValue<string>()  ?? throw new ArgumentException("predicate is required");
        var obj       = args?["object"]?.GetValue<string>()     ?? throw new ArgumentException("object is required");
        var ended     = args?["ended"]?.GetValue<string>();

        _kg.Invalidate(subject, predicate, obj, ended);
        return ToolContent(new { success = true, subject, predicate, @object = obj });
    }

    private JsonNode ToolKgTimeline(JsonNode? args)
    {
        var entity = args?["entity"]?.GetValue<string>();
        var facts  = _kg.Timeline(entity);

        var items = facts.Select(f => new
        {
            subject    = f.Subject,
            predicate  = f.Predicate,
            @object    = f.Object,
            valid_from = f.ValidFrom,
            valid_to   = f.ValidTo
        }).ToList();

        return ToolContent(new { entity = entity ?? "all", count = items.Count, timeline = items });
    }

    private JsonNode ToolKgStats()
    {
        var stats = _kg.Stats();
        return ToolContent(new
        {
            entity_count        = stats.EntityCount,
            triple_count        = stats.TripleCount,
            active_triple_count = stats.ActiveTripleCount,
            type_counts         = stats.TypeCounts
        });
    }

    private JsonNode ToolTraverse(JsonNode? args)
    {
        var startRoom = args?["start_room"]?.GetValue<string>() ?? throw new ArgumentException("start_room is required");
        var maxHops   = args?["max_hops"]?.GetValue<int>() ?? 2;

        var results = _graph.Traverse(startRoom, maxHops);

        var items = results.Select(r => new
        {
            topic  = r.Topic,
            domain = r.Domain,
            hops   = r.Hops
        }).ToList();

        return ToolContent(new { start_room = startRoom, max_hops = maxHops, count = items.Count, topics = items });
    }

    private JsonNode ToolFindLinks(JsonNode? args)
    {
        var domainA = args?["domain_a"]?.GetValue<string>();
        var domainB = args?["domain_b"]?.GetValue<string>();

        var links = _graph.FindLinks(domainA, domainB);

        var items = links.Select(t => new
        {
            topic             = t.Topic,
            connected_domains = t.ConnectedDomains
        }).ToList();

        return ToolContent(new { domain_a = domainA, domain_b = domainB, count = items.Count, links = items });
    }

    private JsonNode ToolLogWrite(JsonNode? args)
    {
        var agentName = args?["agent_name"]?.GetValue<string>() ?? throw new ArgumentException("agent_name is required");
        var entry     = args?["entry"]?.GetValue<string>()      ?? throw new ArgumentException("entry is required");
        var topic     = args?["topic"]?.GetValue<string>()      ?? throw new ArgumentException("topic is required");

        var timestamp     = DateTime.UtcNow.ToString("o");
        var contentWithTs = $"[{timestamp}] [{topic}]\n{entry}";

        var doc = new ChunkRecord
        {
            Id         = Guid.NewGuid().ToString(),
            Domain     = $"log_{agentName}",
            Topic      = "log",
            Category   = "cat_log",
            Content    = contentWithTs,
            AddedBy    = agentName
        };

        _db.AddChunk(doc);
        return ToolContent(new { success = true, chunk_id = doc.Id, agent_name = agentName, topic, filed_at = timestamp });
    }

    private JsonNode ToolLogRead(JsonNode? args)
    {
        var agentName = args?["agent_name"]?.GetValue<string>() ?? throw new ArgumentException("agent_name is required");
        var lastN     = args?["last_n"]?.GetValue<int>() ?? 10;

        var domain = $"log_{agentName}";
        var chunks = _db.GetAllChunks(domain: domain, topic: "log");

        // Take last N entries
        var entries = chunks
            .TakeLast(lastN)
            .Select(d => d.Content)
            .ToList();

        var text = entries.Count > 0
            ? string.Join("\n---\n", entries)
            : $"No log entries found for {agentName}.";

        return ToolContent(new { agent_name = agentName, count = entries.Count, entries = text });
    }

    private JsonNode ToolGetAaakSpec() =>
        ToolContent(_dialect.GetSpec());

    private JsonNode ToolGraphStats()
    {
        var stats = _graph.GetStats();
        return ToolContent(new
        {
            node_count   = stats.NodeCount,
            edge_count   = stats.EdgeCount,
            link_count   = stats.LinkCount,
            domain_count = stats.DomainCount,
            description  =
                "node_count=unique topics; edge_count=topic pairs sharing a domain; " +
                "link_count=topics spanning ≥2 domains; domain_count=distinct domains"
        });
    }
}
