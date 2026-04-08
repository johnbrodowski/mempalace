namespace MemPalace.Examples;

/// <summary>
/// Mirrors Python examples/basic_mining.py
///
/// Shows the init → mine → search workflow using the mempalace CLI.
/// </summary>
public static class BasicMiningExample
{
    public static void Run(string? projectDir = null)
    {
        projectDir ??= "~/projects/my_app";

        Console.WriteLine("Step 1: Initialize rooms from folder structure");
        Console.WriteLine($"  mempalace init {projectDir}");
        Console.WriteLine();
        Console.WriteLine("Step 2: Mine everything");
        Console.WriteLine($"  mempalace mine {projectDir}");
        Console.WriteLine();
        Console.WriteLine("Step 3: Search");
        Console.WriteLine("  mempalace search 'why did we choose this approach'");
    }
}
