namespace MemPalace.Core;

public sealed class AppConfig
{
    private static readonly string MempalaceHome =
        Environment.GetEnvironmentVariable("MEMPALACE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mempalace");

    public string StorePath { get; set; } =
        Environment.GetEnvironmentVariable("MEMPALACE_PALACE_PATH")
        ?? Path.Combine(MempalaceHome, "palace");
    public string IndexPath { get; set; } =
        Environment.GetEnvironmentVariable("MEMPALACE_INDEX_PATH")
        ?? Path.Combine(MempalaceHome, "index");
    public string DbPath { get; set; } =
        Environment.GetEnvironmentVariable("MEMPALACE_DB_PATH")
        ?? Path.Combine(MempalaceHome, "palace.db");
    public string KgDbPath { get; set; } =
        Environment.GetEnvironmentVariable("MEMPALACE_KG_DB_PATH")
        ?? Path.Combine(MempalaceHome, "knowledge_graph.db");
    public string EmbeddingModelDir { get; set; } =
        Environment.GetEnvironmentVariable("MEMPALACE_MODEL_DIR")
        ?? Path.Combine(MempalaceHome, "models");
    public string EntityRegistryPath { get; set; } =
        Path.Combine(MempalaceHome, "entity_registry.json");
    public string IdentityPath { get; set; } =
        Path.Combine(MempalaceHome, "identity.txt");
    public string CollectionName { get; set; } = "mempalace_chunks";
    public string? Domain { get; set; }
    public string Version { get; set; } = "0.1.0-csharp";
}

public sealed class SearchResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Snippet { get; init; }
    public required string Domain { get; init; }
    public required string Topic { get; init; }
    public required string Category { get; init; }
    public double Score { get; init; }
}

public sealed class MemoryDocument
{
    public required string Id { get; init; }
    public required string SourcePath { get; init; }
    public required string Content { get; init; }
    public required string Category { get; init; }
    public required string Topic { get; init; }
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;
}
