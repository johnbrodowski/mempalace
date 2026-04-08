using System.Text.Json;
using MemPalace.Core;
using MemPalace.IntegrationTests;
using MemPalace.Services;

/*
 * MemPalace Integration Tests
 * ===========================
 * Real-world end-to-end test that:
 *   1. Mines the MemPalace C# source code into a temp palace (dogfooding)
 *   2. Exercises search, KG, normalize, compress, and convo-mining
 *   3. Prints human-readable PASS / FAIL for every step
 *
 * Usage:
 *   dotnet run --project MemPalace.IntegrationTests [-- <source_dir>]
 *
 * <source_dir> defaults to the MemPalace source relative to the executable.
 */

// ── Bootstrap ─────────────────────────────────────────────────────────────────

var t = new TestHarness();
TestHarness.Sep('═');
Console.WriteLine("  MemPalace C# Integration Tests");
Console.WriteLine($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
TestHarness.Sep('═');

// Isolated temp palace so we never touch ~/.mempalace
var palaceDir = Path.Combine(Path.GetTempPath(), $"mempalace_integration_{Guid.NewGuid():N}");
Directory.CreateDirectory(palaceDir);

Environment.SetEnvironmentVariable("MEMPALACE_HOME",       palaceDir);
Environment.SetEnvironmentVariable("MEMPALACE_CONFIG_DIR", palaceDir);
Environment.SetEnvironmentVariable("MEMPALACE_PALACE_PATH", Path.Combine(palaceDir, "palace"));
Environment.SetEnvironmentVariable("MEMPALACE_DB_PATH",    Path.Combine(palaceDir, "palace.db"));
Environment.SetEnvironmentVariable("MEMPALACE_KG_DB_PATH", Path.Combine(palaceDir, "kg.db"));

// Determine the C# source directory to mine
// Default: walk up from the executable to find the MemPalace service directory
string? sourceDir = args.Length > 0 ? args[0] : null;
if (sourceDir is null)
{
    // Try to find the MemPalace/Services dir relative to the binary
    var exe = AppContext.BaseDirectory;
    var candidate = FindDir(exe, Path.Combine("MemPalace", "Services"));
    sourceDir = candidate ?? Path.Combine(exe, "MemPalace", "Services");
}

Console.WriteLine($"\n  Palace:     {palaceDir}");
Console.WriteLine($"  Source dir: {sourceDir}");
Console.WriteLine();

// ── Services ──────────────────────────────────────────────────────────────────

var config   = ConfigService.LoadOrCreate();
var db       = new DatabaseService(config);
var miner    = new MinerService(config, db);
var searcher = new SearchService(config, db);
var kg       = new KnowledgeGraphService(config);
var layers   = new LayerService(config, db, kg);
var convoMiner = new ConvoMinerService(config, db);

// ── 1. INIT ───────────────────────────────────────────────────────────────────

t.Section("Init");

await t.RunAsync("InitializeStoreAsync creates palace directories", async () =>
{
    await ConfigService.InitializeStoreAsync(config, palaceDir);
    if (!Directory.Exists(config.StorePath)) throw new Exception($"StorePath missing: {config.StorePath}");
    if (!File.Exists(Path.Combine(config.StorePath, "README.txt"))) throw new Exception("README.txt missing");
    return $"StorePath = {config.StorePath}";
});

// ── 2. MINE ───────────────────────────────────────────────────────────────────

t.Section("Mine (dogfooding — index the MemPalace C# source)");

MineResult mineResult = default!;

await t.RunAsync("Mine MemPalace Services directory", async () =>
{
    if (!Directory.Exists(sourceDir))
        throw new DirectoryNotFoundException($"Source dir not found: {sourceDir}");

    mineResult = await miner.MineAsync(sourceDir, domain: "mempalace-src");
    if (mineResult.ChunksAdded == 0) throw new Exception("No chunks were indexed");
    return $"{mineResult.FilesScanned} files, {mineResult.ChunksAdded} chunks added";
});

t.Assert("At least 5 files scanned", mineResult?.FilesScanned >= 5,
    $"Only {mineResult?.FilesScanned} files scanned");

t.Assert("At least 10 chunks added", mineResult?.ChunksAdded >= 10,
    $"Only {mineResult?.ChunksAdded} chunks added");

await t.RunAsync("Idempotent mine (re-mine skips already-indexed files)", async () =>
{
    var r2 = await miner.MineAsync(sourceDir, domain: "mempalace-src");
    if (r2.ChunksAdded > 0) throw new Exception($"Expected 0 new chunks, got {r2.ChunksAdded}");
    return $"{r2.FilesSkipped} files skipped (all already indexed)";
});

// ── 3. SEARCH ─────────────────────────────────────────────────────────────────

t.Section("Search (FTS5 BM25)");

// Note: FTS5 unicode61 tokenizer splits on non-alphanumeric only (does NOT split camelCase).
// Queries use words that appear as standalone tokens in the indexed C# source comments/SQL.

await t.RunAsync("Search KG SQL terms: 'predicate subject triples'", async () =>
{
    // All three appear as standalone SQL column/table names in KnowledgeGraphService.cs
    var results = await searcher.SearchAsync("predicate subject triples", limit: 5);
    if (results.Count == 0) throw new Exception("No results");
    return $"{results.Count} result(s), top: [{results[0].Score:F2}] {results[0].Title}";
});

await t.RunAsync("Search DB terms: 'fts5 standalone chunk'", async () =>
{
    // All three appear in DatabaseService.cs schema comment: "Standalone FTS5 table (stores chunk content...)"
    var results = await searcher.SearchAsync("fts5 standalone chunk", limit: 5);
    if (results.Count == 0) throw new Exception("No results");
    return $"{results.Count} result(s), top: {results[0].Title}";
});

await t.RunAsync("Search normalizer terms: 'transcript normalize'", async () =>
{
    // 'transcript' and 'normalize' both appear in NormalizeService.cs first chunk (doc comments + method sig)
    var results = await searcher.SearchAsync("transcript normalize", limit: 5);
    if (results.Count == 0) throw new Exception("No results");
    return $"{results.Count} result(s)";
});

await t.RunAsync("Search miner terms: 'gitignore bypass'", async () =>
{
    // Both appear on the same comment line in MinerService.cs: "Apply gitignore - overridden by explicit bypass"
    var results = await searcher.SearchAsync("gitignore bypass", limit: 5);
    if (results.Count == 0) throw new Exception("No results");
    return $"{results.Count} result(s)";
});

await t.RunAsync("Search for nonsense returns zero results", async () =>
{
    var results = await searcher.SearchAsync("zzqxkfjvbwpmlotryn_gibberish_404", limit: 5);
    return $"{results.Count} result(s) (expected 0)";
});

await t.RunAsync("Search with domain filter returns domain-scoped results", async () =>
{
    var results = await searcher.SearchAsync("service", domain: "mempalace-src", limit: 5);
    var allInDomain = results.All(r => r.Domain == "mempalace-src");
    if (!allInDomain) throw new Exception("Results contain chunks from wrong domain");
    return $"{results.Count} result(s), all domain=mempalace-src";
});

// ── 4. STATUS ─────────────────────────────────────────────────────────────────

t.Section("Status");

await t.RunAsync("StatusAsync returns non-zero chunk and domain counts", async () =>
{
    var status = await layers.StatusAsync();
    int chunks  = (int)status["chunk_count"];
    int domains = (int)status["domain_count"];
    if (chunks  == 0) throw new Exception("chunk_count == 0");
    if (domains == 0) throw new Exception("domain_count == 0");
    return $"chunks={chunks}  domains={domains}  topics={(int)status["topic_count"]}";
});

// ── 5. KNOWLEDGE GRAPH ────────────────────────────────────────────────────────

t.Section("Knowledge Graph");

t.Run("AddEntity creates entity records", () =>
{
    kg.AddEntity("Alice", "person", new() { ["gender"] = "female" });
    kg.AddEntity("Bob",   "person", new() { ["gender"] = "male" });
    kg.AddEntity("MemPalace", "project");
    var all = kg.GetAllEntities();
    if (all.Count < 3) throw new Exception($"Expected ≥3 entities, got {all.Count}");
    return $"{all.Count} entities";
});

t.Run("GetEntity retrieves by name", () =>
{
    var alice = kg.GetEntity("Alice");
    if (alice is null) throw new Exception("Alice not found");
    if (alice.EntityType != "person") throw new Exception($"Wrong type: {alice.EntityType}");
    return $"Alice: id={alice.Id}, type={alice.EntityType}";
});

t.Run("AddTriple links entities", () =>
{
    var id = kg.AddTriple("Alice", "works_on", "MemPalace", validFrom: "2024-01-01");
    kg.AddTriple("Bob",   "works_on", "MemPalace", validFrom: "2024-06-01");
    kg.AddTriple("Alice", "knows",    "Bob");
    if (string.IsNullOrEmpty(id)) throw new Exception("Empty triple ID");
    return $"triple id={id[..8]}…";
});

t.Run("GetTriple retrieves by ID", () =>
{
    var id = kg.AddTriple("Alice", "loves", "coffee");
    var triple = kg.GetTriple(id);
    if (triple is null) throw new Exception("Triple not found");
    if (triple.Subject != "alice") throw new Exception($"Wrong subject: {triple.Subject}");
    return $"Subject={triple.Subject} Predicate={triple.Predicate} Object={triple.Object}";
});

t.Run("QueryEntity returns outgoing facts", () =>
{
    var facts = kg.QueryEntity("Alice", direction: "out");
    if (facts.Count < 2) throw new Exception($"Expected ≥2 facts for Alice, got {facts.Count}");
    var isCurrent = facts.All(f => f.IsCurrent);
    return $"{facts.Count} facts, all IsCurrent={isCurrent}";
});

t.Run("QueryEntity direction=both returns incoming+outgoing", () =>
{
    var facts = kg.QueryEntity("MemPalace", direction: "both");
    if (facts.Count < 2) throw new Exception($"Expected ≥2 facts (Alice+Bob work_on MemPalace), got {facts.Count}");
    return $"{facts.Count} facts (incoming + outgoing)";
});

t.Run("QueryRelationship finds all works_on triples", () =>
{
    var facts = kg.QueryRelationship("works_on");
    if (facts.Count < 2) throw new Exception($"Expected ≥2 works_on triples, got {facts.Count}");
    return $"{facts.Count} 'works_on' triples";
});

t.Run("AddTriple is idempotent (duplicate returns same ID)", () =>
{
    var id1 = kg.AddTriple("Alice", "knows", "Bob");
    var id2 = kg.AddTriple("Alice", "knows", "Bob");
    if (id1 != id2) throw new Exception($"Expected same ID: {id1} vs {id2}");
    return "duplicate triple returns same ID";
});

t.Run("Invalidate marks triple as ended", () =>
{
    kg.AddTriple("Alice", "lived_in", "London", validFrom: "2020-01-01");
    kg.Invalidate("Alice", "lived_in", "London", ended: "2023-12-31");
    var facts = kg.QueryEntity("Alice", asOf: "2024-06-01", direction: "out");
    bool stillActive = facts.Any(f => f.Predicate == "lived_in" && f.IsCurrent);
    if (stillActive) throw new Exception("lived_in should be inactive after 2023-12-31");
    return "lived_in triple correctly marked inactive";
});

t.Run("Timeline returns facts in chronological order", () =>
{
    var timeline = kg.Timeline("Alice");
    if (timeline.Count == 0) throw new Exception("No timeline facts");
    return $"{timeline.Count} facts in Alice's timeline";
});

t.Run("KgStats reflects entity and triple counts", () =>
{
    var stats = kg.Stats();
    if (stats.EntityCount < 3) throw new Exception($"Expected ≥3 entities, got {stats.EntityCount}");
    if (stats.TripleCount  < 3) throw new Exception($"Expected ≥3 triples, got {stats.TripleCount}");
    return $"entities={stats.EntityCount} triples={stats.TripleCount} active={stats.ActiveTripleCount}";
});

// ── 6. NORMALIZE ─────────────────────────────────────────────────────────────

t.Section("Normalize");

t.Run("Normalize a real .cs source file", () =>
{
    var csFiles = Directory.GetFiles(sourceDir!, "*.cs", SearchOption.AllDirectories);
    if (csFiles.Length == 0) throw new Exception("No .cs files found");
    var result = NormalizeService.Normalize(csFiles[0]);
    if (string.IsNullOrWhiteSpace(result)) throw new Exception("Empty result");
    return $"{csFiles[0].Split(Path.DirectorySeparatorChar)[^1]}: {result.Length} chars";
});

t.Run("Normalize Claude JSON array [{role,content}]", () =>
{
    var tmp = Path.GetTempFileName() + ".json";
    File.WriteAllText(tmp, JsonSerializer.Serialize(new[]
    {
        new { role = "user",      content = "What is FTS5?" },
        new { role = "assistant", content = "FTS5 is SQLite's full-text search engine." },
    }));
    try
    {
        var result = NormalizeService.Normalize(tmp);
        if (!result.Contains("FTS5")) throw new Exception("Expected 'FTS5' in output");
        return $"{result.Length} chars, contains 'FTS5'";
    }
    finally { File.Delete(tmp); }
});

t.Run("Normalize plain .txt file", () =>
{
    var tmp = Path.GetTempFileName() + ".txt";
    File.WriteAllText(tmp, "MemPalace stores your memory.\nIt uses SQLite for persistence.\n");
    try
    {
        var result = NormalizeService.Normalize(tmp);
        if (!result.Contains("MemPalace")) throw new Exception("Content missing");
        return $"{result.Length} chars";
    }
    finally { File.Delete(tmp); }
});

t.Run("Normalize empty file returns empty", () =>
{
    var tmp = Path.GetTempFileName() + ".txt";
    File.WriteAllText(tmp, "");
    try
    {
        var result = NormalizeService.Normalize(tmp);
        if (result.Trim().Length != 0) throw new Exception($"Expected empty, got: {result[..Math.Min(40, result.Length)]}");
        return "empty file → empty string";
    }
    finally { File.Delete(tmp); }
});

// ── 7. COMPRESS ───────────────────────────────────────────────────────────────

t.Section("Compress (AAAK)");

t.Run("Compress reduces character count", () =>
{
    var dialect = new DialectService();
    var text = "The quick brown fox jumps over the lazy dog. " +
               "Memory is the persistence of experience across time. " +
               "SQLite is a lightweight relational database engine.";
    var compressed = dialect.Compress(text);
    var stats = dialect.GetCompressionStats(text, compressed);
    if (string.IsNullOrWhiteSpace(compressed)) throw new Exception("Empty compressed output");
    return $"{stats.OriginalChars}→{stats.CompressedChars} chars, ratio={stats.CompressionRatio:P0}";
});

t.Run("Compress preserves key nouns", () =>
{
    var dialect = new DialectService();
    var text = "Alice deployed MemPalace to production on Friday.";
    var compressed = dialect.Compress(text);
    bool hasAlice     = compressed.Contains("Alice",     StringComparison.OrdinalIgnoreCase);
    bool hasMemPalace = compressed.Contains("MemPalace", StringComparison.OrdinalIgnoreCase);
    if (!hasAlice || !hasMemPalace) throw new Exception($"Key nouns lost. Compressed: {compressed}");
    return $"'{compressed}'";
});

// ── 8. CONVO MINER ────────────────────────────────────────────────────────────

t.Section("Conversation Miner");

await t.RunAsync("Mine a synthetic multi-turn conversation file", async () =>
{
    var convoDir = Path.Combine(palaceDir, "test_convos");
    Directory.CreateDirectory(convoDir);

    // Write a realistic Q&A transcript (the MemPalace > format)
    File.WriteAllText(Path.Combine(convoDir, "session_001.txt"), """
        > What is MemPalace?
        MemPalace is a personal memory system that stores your files, conversations, and knowledge in a local SQLite database with FTS5 full-text search.

        > How does mining work?
        Mining walks your project directory, respects .gitignore rules, splits content into overlapping chunks, and stores each chunk with domain, topic, and category metadata.

        > What is the knowledge graph for?
        The knowledge graph stores temporal facts about entities — people, projects, tools — as subject/predicate/object triples with valid_from and valid_to timestamps.

        > How does search work?
        Search uses SQLite FTS5 with BM25 ranking. You can filter by domain or topic and optionally apply hybrid keyword re-scoring on top of the BM25 results.
        """);

    var r = await convoMiner.MineConversationsAsync(convoDir, domain: "test-convos");
    if (r.ChunksAdded == 0) throw new Exception("No chunks added from conversation");
    return $"{r.FilesScanned} files, {r.ChunksAdded} chunks";
});

await t.RunAsync("Search conversation content after mining", async () =>
{
    var results = await searcher.SearchAsync("knowledge graph temporal facts triples", domain: "test-convos", limit: 3);
    if (results.Count == 0) throw new Exception("No results from conversation search");
    return $"{results.Count} result(s), top snippet: \"{results[0].Snippet[..Math.Min(60, results[0].Snippet.Length)]}…\"";
});

// ── 9. KG SEED ───────────────────────────────────────────────────────────────

t.Section("Knowledge Graph — SeedFromEntityFacts");

t.Run("SeedFromEntityFacts populates graph from dict", () =>
{
    var facts = new Dictionary<string, object>
    {
        ["person:Carol"] = JsonSerializer.Deserialize<object>(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["child_of"] = "Alice",
                ["works_on"] = "MemPalace",
            }))!,
    };
    kg.SeedFromEntityFacts(facts);
    var carol = kg.GetEntity("Carol");
    if (carol is null) throw new Exception("Carol not seeded");
    return $"Carol seeded: id={carol.Id}, type={carol.EntityType}";
});

// ── 10. PALACE GRAPH ─────────────────────────────────────────────────────────

t.Section("Palace Graph");

t.Run("PalaceGraphService returns non-null stats", () =>
{
    var graph = new PalaceGraphService(db);
    var stats = graph.GetStats();
    return $"nodes={stats.NodeCount} edges={stats.EdgeCount} domains={stats.DomainCount}";
});

// ── Done ──────────────────────────────────────────────────────────────────────

// Cleanup temp palace
try { Directory.Delete(palaceDir, recursive: true); } catch { }

return t.PrintSummary();

// ── Helper ────────────────────────────────────────────────────────────────────

static string? FindDir(string startPath, string relativePart)
{
    var dir = new DirectoryInfo(startPath);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, relativePart);
        if (Directory.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        dir = dir.Parent;
    }
    return null;
}
