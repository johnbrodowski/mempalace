using MemPalace.Core;
using Xunit;

namespace MemPalace.Tests;

/// <summary>
/// Mirrors Python tests/test_convo_miner.py
/// </summary>
public sealed class ConvoMinerServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly AppConfig _config;
    private readonly DatabaseService _db;
    private readonly ConvoMinerService _miner;

    public ConvoMinerServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"mempalace_convo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);

        // DB lives outside _tmpDir so Windows WAL file locks don't block directory deletion
        var dbPath = Path.Combine(Path.GetTempPath(), $"mempalace_convo_db_{Guid.NewGuid():N}.db");
        _config = new AppConfig
        {
            StorePath = _tmpDir,
            IndexPath = _tmpDir,
            DbPath    = dbPath,
        };
        _db    = new DatabaseService(_config);
        _miner = new ConvoMinerService(_config, _db);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
        // DB file in OS temp root — let the OS clean it up
    }

    // Mirrors: test_convo_mining
    [Fact]
    public async Task ConvoMining_IndexesConversationChunks()
    {
        // Write a simple Q&A conversation file (same format as Python test)
        var convoDir = Path.Combine(_tmpDir, "convos");
        Directory.CreateDirectory(convoDir);
        File.WriteAllText(
            Path.Combine(convoDir, "chat.txt"),
            "> What is memory?\nMemory is persistence.\n\n" +
            "> Why does it matter?\nIt enables continuity.\n\n" +
            "> How do we build it?\nWith structured storage.\n");

        var result = await _miner.MineConversationsAsync(convoDir, domain: "test_convos");

        Assert.True(result.ChunksAdded >= 1, "Expected at least one chunk to be indexed");

        // Verify search works (mirrors: col.query(query_texts=["memory persistence"], n_results=1))
        var searchResults = _db.SearchFts5("memory persistence", limit: 1);
        Assert.True(searchResults.Count > 0, "Expected at least one search result");
    }
}
