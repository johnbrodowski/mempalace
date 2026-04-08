using System.Security.Cryptography;
using System.Text;

namespace MemPalace.Core;

public sealed class ConvoMinerService
{
    private static readonly HashSet<string> ConvoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl"
    };

    private readonly AppConfig _config;
    private readonly DatabaseService _db;

    public ConvoMinerService(AppConfig config, DatabaseService db)
    {
        _config = config;
        _db = db;
    }

    public async Task<ConvoMineResult> MineConversationsAsync(
        string convoDir,
        string? domain = null,
        int limit = 0,
        bool dryRun = false,
        string extractMode = "exchange")
    {
        string effectiveDomain = domain ?? "conversations";

        int filesScanned = 0;
        int chunksAdded = 0;
        int filesSkipped = 0;
        var topicCounts = new Dictionary<string, int>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(convoDir, "*", SearchOption.AllDirectories)
                .Where(f => ConvoExtensions.Contains(Path.GetExtension(f)));
        }
        catch
        {
            return new ConvoMineResult
            {
                FilesScanned = 0,
                ChunksAdded = 0,
                FilesSkipped = 0,
                TopicCounts = topicCounts
            };
        }

        foreach (var filePath in files)
        {
            if (limit > 0 && filesScanned >= limit)
                break;

            if (_db.ChunkExists(filePath))
            {
                filesSkipped++;
                continue;
            }

            string transcript;
            try
            {
                transcript = await Task.Run(() => NormalizeService.Normalize(filePath));
            }
            catch
            {
                filesSkipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                filesSkipped++;
                continue;
            }

            filesScanned++;

            IReadOnlyList<ConvoSegment> chunks = extractMode == "paragraph"
                ? ChunkByParagraph(transcript)
                : ChunkByExchange(transcript);

            foreach (var chunk in chunks)
            {
                string idSource = filePath + chunk.ChunkIndex.ToString();
                string id = ComputeSha256Hex(idSource);

                var doc = new ChunkRecord
                {
                    Id = id,
                    Domain = effectiveDomain,
                    Topic = chunk.Topic,
                    Category = chunk.Category,
                    SourceFile = filePath,
                    Content = chunk.Content,
                    ChunkIndex = chunk.ChunkIndex,
                    AddedBy = "mempalace-convo-miner",
                    FiledAt = DateTime.UtcNow.ToString("o")
                };

                if (!dryRun)
                    _db.AddChunk(doc);

                chunksAdded++;
                topicCounts[chunk.Topic] = topicCounts.GetValueOrDefault(chunk.Topic) + 1;
            }
        }

        return new ConvoMineResult
        {
            FilesScanned = filesScanned,
            ChunksAdded = chunksAdded,
            FilesSkipped = filesSkipped,
            TopicCounts = topicCounts
        };
    }

    public IReadOnlyList<ConvoSegment> ChunkByExchange(string normalizedTranscript)
    {
        var chunks = new List<ConvoSegment>();
        var lines = normalizedTranscript.Split('\n');

        int chunkIndex = 0;
        int i = 0;

        while (i < lines.Length)
        {
            // Find the next user turn
            if (!lines[i].StartsWith("> "))
            {
                i++;
                continue;
            }

            // Collect consecutive user lines (multi-line user turns)
            var userLines = new List<string>();
            while (i < lines.Length && lines[i].StartsWith("> "))
            {
                userLines.Add(lines[i][2..]); // strip "> "
                i++;
            }

            // Collect up to 8 AI response lines (non-user, non-empty after user turn)
            var aiLines = new List<string>();
            int aiCount = 0;
            while (i < lines.Length && !lines[i].StartsWith("> ") && aiCount < 8)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length > 0)
                {
                    aiLines.Add(trimmed);
                    aiCount++;
                }
                i++;
            }

            // Build chunk content: user turn + AI response
            var sb = new StringBuilder();
            sb.Append("> ");
            sb.AppendLine(string.Join(" ", userLines));
            if (aiLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(string.Join("\n", aiLines));
            }

            string content = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            string topic = RoomDetectorService.DetectConvoTopic(content);
            string category = RoomDetectorService.DetectCategory(content);

            chunks.Add(new ConvoSegment
            {
                Content = content,
                Topic = topic,
                Category = category,
                ChunkIndex = chunkIndex++
            });
        }

        return chunks;
    }

    public IReadOnlyList<ConvoSegment> ChunkByParagraph(string content)
    {
        var chunks = new List<ConvoSegment>();
        var paragraphs = content.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

        int chunkIndex = 0;
        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (trimmed.Length <= 50)
                continue;

            string topic = RoomDetectorService.DetectConvoTopic(trimmed);
            string category = RoomDetectorService.DetectCategory(trimmed);

            chunks.Add(new ConvoSegment
            {
                Content = trimmed,
                Topic = topic,
                Category = category,
                ChunkIndex = chunkIndex++
            });
        }

        return chunks;
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class ConvoMineResult
{
    public int FilesScanned { get; init; }
    public int ChunksAdded { get; init; }
    public int FilesSkipped { get; init; }
    public Dictionary<string, int> TopicCounts { get; init; } = new();
}
