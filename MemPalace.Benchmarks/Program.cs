using MemPalace.Benchmarks;

/*
 * MemPalace Benchmarks CLI
 * ========================
 * Mirrors the Python benchmark scripts in benchmarks/
 *
 * Sub-commands
 * ────────────
 *   membench   <data_dir>  [--category X] [--topic X] [--top-k N] [--mode raw|hybrid] [--limit N] [--out FILE]
 *   convomem               [--limit N] [--category X] [--cache-dir DIR] [--top-k N]
 *   locomo     <data_file> [--top-k N] [--mode raw|hybrid] [--out FILE]
 *   longmemeval <data_file>[--top-k N] [--mode raw|hybrid] [--limit N] [--out FILE]
 */

if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "membench"    => RunMemBench(args[1..]),
    "convomem"    => await RunConvoMemAsync(args[1..]),
    "locomo"      => RunLoCoMo(args[1..]),
    "longmemeval" => RunLongMemEval(args[1..]),
    _             => Unknown(args[0]),
};

// ── MemBench ─────────────────────────────────────────────────────────────────

static int RunMemBench(string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: membench <data_dir> [options]"); return 1; }
    var dataDir  = args[0];
    var category = Flag(args, "--category");
    var topic    = Flag(args, "--topic")    ?? "movie";
    var topK     = Int(args, "--top-k",    5);
    var limit    = Int(args, "--limit",    0);
    var mode     = Flag(args, "--mode")     ?? "hybrid";
    var outFile  = Flag(args, "--out");

    if (outFile is null)
    {
        var catTag = category is not null ? $"_{category}" : "_all";
        outFile = $"results_membench_{mode}{catTag}_{topic}_top{topK}_{DateTime.Now:yyyyMMdd_HHmm}.json";
    }

    MemBenchRunner.Run(dataDir, category, topic, topK, limit, mode, outFile);
    return 0;
}

// ── ConvoMem ─────────────────────────────────────────────────────────────────

static async Task<int> RunConvoMemAsync(string[] args)
{
    var limit    = Int(args, "--limit",   100);
    var category = Flag(args, "--category");
    var cacheDir = Flag(args, "--cache-dir") ?? "";
    var topK     = Int(args, "--top-k",   5);

    await ConvoMemRunner.RunAsync(limit, category, cacheDir, topK);
    return 0;
}

// ── LoCoMo ───────────────────────────────────────────────────────────────────

static int RunLoCoMo(string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: locomo <data_file.json> [options]"); return 1; }
    var dataFile = args[0];
    var topK     = Int(args,  "--top-k", 5);
    var mode     = Flag(args, "--mode")  ?? "hybrid";
    var outFile  = Flag(args, "--out");

    LoCoMoRunner.Run(dataFile, topK, mode, outFile);
    return 0;
}

// ── LongMemEval ──────────────────────────────────────────────────────────────

static int RunLongMemEval(string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: longmemeval <data_file.json> [options]"); return 1; }
    var dataFile = args[0];
    var topK     = Int(args,  "--top-k", 5);
    var mode     = Flag(args, "--mode")  ?? "hybrid";
    var limit    = Int(args,  "--limit", 0);
    var outFile  = Flag(args, "--out");

    LongMemEvalRunner.Run(dataFile, topK, mode, limit, outFile);
    return 0;
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static string? Flag(string[] a, string name)
{
    var idx = Array.IndexOf(a, name);
    return idx >= 0 && idx + 1 < a.Length ? a[idx + 1] : null;
}

static int Int(string[] a, string name, int def)
    => int.TryParse(Flag(a, name), out var v) ? v : def;

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"Unknown sub-command: {cmd}");
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        MemPalace Benchmarks
        Usage: mempalace-bench <sub-command> [options]

        Sub-commands:
          membench   <data_dir>   Run MemBench (ACL 2025) retrieval recall benchmark
          convomem                Run ConvoMem (Salesforce) benchmark  (downloads data)
          locomo     <data_file>  Run LoCoMo long-context memory benchmark
          longmemeval <data_file> Run LongMemEval benchmark

        Common options:
          --top-k N      Retrieval top-K  (default: 5)
          --mode MODE    raw or hybrid    (default: hybrid)
          --limit N      Max items        (default: all)
          --out FILE     Output JSON file

        MemBench options:
          --category X   One of: simple, highlevel, knowledge_update, ...
          --topic X      movie | food | book  (default: movie)

        ConvoMem options:
          --category X   One of the 6 evidence categories
          --cache-dir D  Local cache dir for downloaded files
        """);
}
