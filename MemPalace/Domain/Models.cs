namespace MemPalace.Core;

public sealed class ChunkRecord
{
    public required string Id { get; init; }
    public required string Domain { get; init; }
    public required string Topic { get; init; }
    public string Category { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public required string Content { get; init; }
    public int ChunkIndex { get; init; }
    public string AddedBy { get; init; } = "mempalace";
    public string FiledAt { get; init; } = DateTime.UtcNow.ToString("o");
}

public sealed class KgEntity
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string EntityType { get; init; } = "unknown";
    public string PropertiesJson { get; init; } = "{}";
    public string CreatedAt { get; init; } = DateTime.UtcNow.ToString("o");
}

public sealed class KgTriple
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required string Predicate { get; init; }
    public required string Object { get; init; }
    public string? ValidFrom { get; init; }
    public string? ValidTo { get; init; }
    public double Confidence { get; init; } = 1.0;
    public string? SourceRef { get; init; }
    public string? SourceFile { get; init; }
    public string ExtractedAt { get; init; } = DateTime.UtcNow.ToString("o");
}

public sealed class KgFact
{
    public required string Subject { get; init; }
    public required string Predicate { get; init; }
    public required string Object { get; init; }
    public string? ValidFrom { get; init; }
    public string? ValidTo { get; init; }
    public double Confidence { get; init; }
    public string? SourceFile { get; init; }
    public string? SourceRef { get; init; }  // Python's source_closet
    public bool IsCurrent { get; init; }     // true when valid_to IS NULL
}

public sealed class KgStats
{
    public int EntityCount { get; init; }
    public int TripleCount { get; init; }
    public int ActiveTripleCount { get; init; }
    public Dictionary<string, int> TypeCounts { get; init; } = new();
}

public sealed class MemoryChunk
{
    public required string Content { get; init; }
    public required string MemoryType { get; init; }
    public int ChunkIndex { get; init; }
}

public sealed class TopicNode
{
    public required string Topic { get; init; }
    public HashSet<string> Domains { get; init; } = new();
    public HashSet<string> Categories { get; init; } = new();
    public int ChunkCount { get; init; }
}

public sealed class GraphEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Via { get; init; }
}

public sealed class GraphStats
{
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    public int LinkCount { get; init; }
    public int DomainCount { get; init; }
}

public sealed class CompressionStats
{
    public int OriginalChars { get; init; }
    public int CompressedChars { get; init; }
    public int OriginalTokens { get; init; }
    public int CompressedTokens { get; init; }
    public double CompressionRatio { get; init; }
}

public sealed class TraversalResult
{
    public required string Topic { get; init; }
    public required string Domain { get; init; }
    public int Hops { get; init; }
}

public sealed class LinkResult
{
    public required string Topic { get; init; }
    public IReadOnlyList<string> ConnectedDomains { get; init; } = [];
}

public sealed class EntityInfo
{
    public string Source { get; init; } = "learned";
    public List<string> Contexts { get; init; } = new();
    public List<string> Aliases { get; init; } = new();
    public string Relationship { get; init; } = "unknown";
    public double Confidence { get; init; } = 0.5;
}

public sealed class EntityLookupResult
{
    public required string Word { get; init; }
    public required string Classification { get; init; } // "person", "project", "common", "unknown"
    public double Confidence { get; init; }
    public string? Relationship { get; init; }
}

public sealed class DetectedEntities
{
    public IReadOnlyList<string> People { get; init; } = [];
    public IReadOnlyList<string> Projects { get; init; } = [];
    public IReadOnlyList<string> Uncertain { get; init; } = [];
}

public sealed class ConvoSegment
{
    public required string Content { get; init; }
    public required string Topic { get; init; }
    public string Category { get; init; } = "";
    public int ChunkIndex { get; init; }
}

public sealed class PersonEntry
{
    public required string Name { get; init; }
    public string Relationship { get; init; } = "unknown";
    public string Code { get; init; } = "";
}

/// <summary>
/// One row from chunk_embeddings joined with its parent chunk metadata.
/// Returned by <see cref="DatabaseService.GetEmbeddingsWithMeta"/>.
/// </summary>
public sealed record EmbeddingRow(
    string  ChunkId,
    float[] Embedding,
    string  Domain,
    string  Topic,
    string  Title,
    string  Snippet
);
