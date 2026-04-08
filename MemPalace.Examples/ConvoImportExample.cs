namespace MemPalace.Examples;

/// <summary>
/// Mirrors Python examples/convo_import.py
///
/// Shows how to import Claude Code and ChatGPT conversation exports.
/// </summary>
public static class ConvoImportExample
{
    public static void Run()
    {
        Console.WriteLine("Import Claude Code sessions:");
        Console.WriteLine("  mempalace mine ~/claude-sessions/ --mode convos --wing my_project");
        Console.WriteLine();
        Console.WriteLine("Import ChatGPT exports:");
        Console.WriteLine("  mempalace mine ~/chatgpt-exports/ --mode convos");
        Console.WriteLine();
        Console.WriteLine("Use general extractor for richer extraction:");
        Console.WriteLine("  mempalace mine ~/chats/ --mode convos --extract general");
    }
}
