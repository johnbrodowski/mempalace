using System.Text.Json;
using MemPalace.Core;
using Xunit;

namespace MemPalace.Tests;

/// <summary>
/// Mirrors Python tests/test_normalize.py
/// </summary>
public sealed class NormalizeServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private string WriteTempFile(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mempalace_norm_{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    // Mirrors: test_plain_text
    [Fact]
    public void Normalize_PlainTextFile_ReturnsContent()
    {
        var path = WriteTempFile(".txt", "Hello world\nSecond line\n");

        var result = NormalizeService.Normalize(path);

        Assert.Contains("Hello world", result);
    }

    // Mirrors: test_claude_json — JSON array of {role, content} objects
    [Fact]
    public void Normalize_ClaudeJsonFormat_ExtractsMessages()
    {
        var data = new[]
        {
            new { role = "user",      content = "Hi"    },
            new { role = "assistant", content = "Hello" },
        };
        var path = WriteTempFile(".json", JsonSerializer.Serialize(data));

        var result = NormalizeService.Normalize(path);

        Assert.Contains("Hi", result);
    }

    // Mirrors: test_empty
    [Fact]
    public void Normalize_EmptyFile_ReturnsEmptyOrWhitespace()
    {
        var path = WriteTempFile(".txt", "");

        var result = NormalizeService.Normalize(path);

        Assert.True(result.Trim() == string.Empty,
            $"Expected empty result but got: '{result}'");
    }
}
