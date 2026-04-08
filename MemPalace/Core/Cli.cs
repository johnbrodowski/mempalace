using MemPalace.Services;

namespace MemPalace.Core;

public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var config = ConfigService.LoadOrCreate();

            // ── Semantic embedding bootstrap (best-effort, non-fatal) ─────────
            EmbeddingService? embedder = null;
            if (!args.Contains("--no-embed"))
            {
                try
                {
                    if (!ModelDownloader.IsAvailable(config.EmbeddingModelDir))
                        await ModelDownloader.EnsureModelAsync(config.EmbeddingModelDir);

                    embedder = await EmbeddingService.TryCreateAsync(config.EmbeddingModelDir);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[embedding] Could not load model: {ex.Message}. Falling back to BM25-only search.");
                }
            }

            var db      = new DatabaseService(config);
            var miner   = new MinerService(config, db, embedder);
            var searcher = new SearchService(config, db, embedder);
            var kg      = new KnowledgeGraphService(config);
            var graph   = new PalaceGraphService(db);
            var layers  = new LayerService(config, db, kg);

            if (args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "init"     => await RunInit(config, db, args),
                "mine"     => await RunMine(config, db, miner, args),
                "search"   => await RunSearch(searcher, args),
                "status"   => await RunStatus(config, db, layers),
                "wake-up"  => await RunWakeUp(layers, args),
                "compress" => RunCompress(args),
                "split"    => await RunSplit(args),
                "repair"   => RunRepair(db),
                "onboard"  => await RunOnboard(config, db, args),
                "mcp"      => await RunMcp(config, db, kg, searcher, graph, layers),
                _          => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // ── init ────────────────────────────────────────────────────────────────

    private static async Task<int> RunInit(AppConfig config, DatabaseService db, string[] args)
    {
        var target = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : Environment.CurrentDirectory;
        await ConfigService.InitializeStoreAsync(config, target);
        Console.WriteLine($"Initialized MemPalace at {config.StorePath}");
        Console.WriteLine($"Database: {config.DbPath}");
        Console.WriteLine($"Knowledge graph: {config.KgDbPath}");
        return 0;
    }

    // ── mine ─────────────────────────────────────────────────────────────────

    private static async Task<int> RunMine(AppConfig config, DatabaseService db, MinerService miner, string[] args)
    {
        if (args.Length < 2 || args[1].StartsWith('-'))
        {
            Console.Error.WriteLine("Usage: mempalace mine <path> [--mode projects|convos|general] [--domain <name>] [--limit <n>] [--dry-run]");
            return 1;
        }

        var path = args[1];
        var mode = ParseFlag(args, "--mode") ?? "projects";
        var domain = ParseFlag(args, "--domain");
        var limitStr = ParseFlag(args, "--limit");
        var limit = limitStr is not null && int.TryParse(limitStr, out var l) ? l : 0;
        var dryRun = args.Contains("--dry-run");

        if (mode == "convos")
        {
            var convoMiner = new ConvoMinerService(config, db);
            var r = await convoMiner.MineConversationsAsync(path, domain, limit, dryRun);
            Console.WriteLine($"Conversations: {r.FilesScanned} files, {r.ChunksAdded} chunks added, {r.FilesSkipped} skipped.");
            foreach (var (topic, count) in r.TopicCounts.OrderByDescending(x => x.Value))
                Console.WriteLine($"  {topic}: {count}");
        }
        else
        {
            var r = await miner.MineAsync(path, mode, domain, limit, dryRun);
            Console.WriteLine($"Mined: {r.FilesScanned} files, {r.ChunksAdded} chunks added, {r.FilesSkipped} skipped.");
            foreach (var (topic, count) in r.TopicCounts.OrderByDescending(x => x.Value))
                Console.WriteLine($"  {topic}: {count}");
        }
        return 0;
    }

    // ── search ───────────────────────────────────────────────────────────────

    private static async Task<int> RunSearch(SearchService searcher, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: mempalace search <query> [--domain <name>] [--topic <name>] [--limit <n>]");
            return 1;
        }

        var domain = ParseFlag(args, "--domain");
        var topic  = ParseFlag(args, "--topic");
        var limitS = ParseFlag(args, "--limit");
        var limit  = limitS is not null && int.TryParse(limitS, out var l) ? l : 10;

        // Query is everything before the first flag
        var queryTokens = args.Skip(1).TakeWhile(a => !a.StartsWith('-'));
        var query = string.Join(' ', queryTokens);

        var results = await searcher.SearchAsync(query, domain, topic, limit);
        if (results.Count == 0)
        {
            Console.WriteLine("No results found.");
            return 0;
        }

        foreach (var r in results)
            Console.WriteLine($"[{r.Score:F2}] ({r.Domain}/{r.Topic}) {r.Title}\n  {r.Snippet}\n");

        return 0;
    }

    // ── status ───────────────────────────────────────────────────────────────

    private static async Task<int> RunStatus(AppConfig config, DatabaseService db, LayerService layers)
    {
        var status = await layers.StatusAsync();
        Console.WriteLine($"MemPalace v{config.Version}");
        Console.WriteLine($"Store DB:     {config.DbPath}");
        Console.WriteLine($"KG DB:        {config.KgDbPath}");
        Console.WriteLine($"Chunks:       {status["chunk_count"]}");
        Console.WriteLine($"Domains:      {status["domain_count"]}");
        Console.WriteLine($"Topics:       {status["topic_count"]}");
        Console.WriteLine($"Identity:     {(bool)status["has_identity"]}");
        var kg = (KgStats)status["kg_stats"];
        Console.WriteLine($"KG entities:  {kg.EntityCount}  triples: {kg.TripleCount} (active: {kg.ActiveTripleCount})");
        return 0;
    }

    // ── wake-up ──────────────────────────────────────────────────────────────

    private static async Task<int> RunWakeUp(LayerService layers, string[] args)
    {
        var domain = ParseFlag(args, "--domain");
        var text = await layers.WakeUpAsync(domain);
        Console.WriteLine(text);
        return 0;
    }

    // ── compress ─────────────────────────────────────────────────────────────

    private static int RunCompress(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: mempalace compress <text_or_file>");
            return 1;
        }

        var input = args[1];
        string text;
        if (File.Exists(input))
            text = File.ReadAllText(input);
        else
            text = string.Join(' ', args.Skip(1));

        var dialect = new DialectService();
        var compressed = dialect.Compress(text);
        var stats = dialect.GetCompressionStats(text, compressed);
        Console.WriteLine(compressed);
        Console.WriteLine();
        Console.WriteLine($"Original: {stats.OriginalChars} chars (~{stats.OriginalTokens} tokens)");
        Console.WriteLine($"Compressed: {stats.CompressedChars} chars (~{stats.CompressedTokens} tokens)  ratio: {stats.CompressionRatio:P0}");
        return 0;
    }

    // ── split ────────────────────────────────────────────────────────────────

    private static async Task<int> RunSplit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: mempalace split <dir_or_file> [--output <dir>] [--dry-run]");
            return 1;
        }

        var path    = args[1];
        var output  = ParseFlag(args, "--output");
        var dryRun  = args.Contains("--dry-run");
        var svc     = new SplitMegaFilesService();

        IReadOnlyList<string> created;
        if (File.Exists(path))
            created = await svc.SplitFileAsync(path, output, dryRun);
        else
            created = await svc.SplitDirectoryAsync(path, output, dryRun: dryRun);

        if (created.Count == 0)
        {
            Console.WriteLine("No multi-session files found.");
            return 0;
        }

        Console.WriteLine(dryRun ? "Would create:" : "Created:");
        foreach (var f in created) Console.WriteLine($"  {f}");
        return 0;
    }

    // ── repair ───────────────────────────────────────────────────────────────

    private static int RunRepair(DatabaseService db)
    {
        Console.Write("Rebuilding FTS5 index… ");
        db.RebuildFts();
        Console.WriteLine("done.");
        return 0;
    }

    // ── onboard ──────────────────────────────────────────────────────────────

    private static async Task<int> RunOnboard(AppConfig config, DatabaseService db, string[] args)
    {
        var directory   = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : Environment.CurrentDirectory;
        var noDetect    = args.Contains("--no-detect");
        var svc         = new OnboardingService(config, db);
        var registry    = await svc.RunOnboardingAsync(directory, autoDetect: !noDetect);
        Console.WriteLine(registry.Summary());
        return 0;
    }

    // ── mcp ──────────────────────────────────────────────────────────────────

    private static async Task<int> RunMcp(AppConfig config, DatabaseService db, KnowledgeGraphService kg,
        SearchService searcher, PalaceGraphService graph, LayerService layers)
    {
        var dialect = new DialectService();
        var server  = new McpServerService(config, db, kg, searcher, graph, layers, dialect);
        await server.RunAsync();
        return 0;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? ParseFlag(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MemPalace (C# port)

            Commands:
              init   [path]               Initialize store directories
              mine   <path> [flags]       Index files into the store
                --mode   projects|convos|general  (default: projects)
                --domain <name>           Assign domain name
                --limit  <n>              Max files to process
                --dry-run                 Preview without writing
              search <query> [flags]      Search store chunks (hybrid BM25 + semantic when model present)
                --domain <name>           Filter by domain
                --topic  <name>           Filter by topic
                --limit <n>               Max results (default: 10)
              --no-embed                  Skip loading the embedding model (BM25-only, faster cold start)
              status                      Show store stats
              wake-up [--domain <name>]   Print 4-layer memory context
              compress <text|file>        AAAK-compress text
              split  <dir|file> [flags]   Split multi-session transcripts
                --output <dir>            Output directory
                --dry-run                 Preview only
              repair                      Rebuild FTS5 search index
              onboard [dir] [--no-detect] Run first-time setup wizard
              mcp                         Start MCP server (stdin/stdout)
              help                        Show this help
            """);
    }
}
