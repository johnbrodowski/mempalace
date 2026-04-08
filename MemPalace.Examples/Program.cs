using MemPalace.Examples;

/*
 * MemPalace Examples
 * ==================
 * Mirrors Python examples/ scripts.
 *
 * Usage:
 *   mempalace-examples basic-mining  [project_dir]
 *   mempalace-examples convo-import
 */

var example = args.Length > 0 ? args[0].ToLowerInvariant() : "basic-mining";

switch (example)
{
    case "basic-mining":
        BasicMiningExample.Run(args.Length > 1 ? args[1] : null);
        break;

    case "convo-import":
        ConvoImportExample.Run();
        break;

    default:
        Console.Error.WriteLine($"Unknown example: {example}");
        Console.WriteLine("Available examples:");
        Console.WriteLine("  basic-mining  [project_dir]   -- init → mine → search workflow");
        Console.WriteLine("  convo-import                  -- import Claude / ChatGPT conversations");
        Environment.Exit(1);
        break;
}
