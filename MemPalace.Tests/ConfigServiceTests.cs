using System.Text.Json;
using MemPalace.Core;
using MemPalace.Services;
using Xunit;

namespace MemPalace.Tests;

/// <summary>
/// Mirrors Python tests/test_config.py
/// </summary>
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _tmpDir;

    public ConfigServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"mempalace_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // Mirrors: test_default_config
    [Fact]
    public void DefaultConfig_HasExpectedPaths()
    {
        var cfg = new AppConfig();
        Assert.Contains("palace", cfg.StorePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("mempalace_chunks", cfg.CollectionName);
    }

    // Mirrors: test_config_from_file — deserialise a JSON file with custom values
    [Fact]
    public void Config_CanRoundTripThroughJson()
    {
        var customPath = Path.Combine(_tmpDir, "custom_palace");
        var original = new AppConfig { StorePath = customPath };
        var json = JsonSerializer.Serialize(original);

        var loaded = JsonSerializer.Deserialize<AppConfig>(json)!;

        Assert.Equal(customPath, loaded.StorePath);
    }

    // Mirrors: test_env_override — MEMPALACE_PALACE_PATH overrides StorePath
    [Fact]
    public void Config_EnvVar_OverridesStorePath()
    {
        var envPath = Path.Combine(_tmpDir, "env_palace");
        Environment.SetEnvironmentVariable("MEMPALACE_PALACE_PATH", envPath);
        try
        {
            var cfg = new AppConfig();
            Assert.Equal(envPath, cfg.StorePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEMPALACE_PALACE_PATH", null);
        }
    }

    // Mirrors: test_init — ConfigService.InitializeStoreAsync creates the store directory and README
    [Fact]
    public async Task InitializeStoreAsync_CreatesStoreAndReadme()
    {
        Environment.SetEnvironmentVariable("MEMPALACE_CONFIG_DIR", _tmpDir);
        try
        {
            var storeDir = Path.Combine(_tmpDir, "palace");
            var indexDir = Path.Combine(_tmpDir, "index");
            var cfg = new AppConfig
            {
                StorePath = storeDir,
                IndexPath = indexDir,
                DbPath    = Path.Combine(_tmpDir, "test.db"),
            };

            await ConfigService.InitializeStoreAsync(cfg, _tmpDir);

            Assert.True(Directory.Exists(storeDir));
            Assert.True(File.Exists(Path.Combine(storeDir, "README.txt")));
            Assert.True(File.Exists(Path.Combine(_tmpDir, "config.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEMPALACE_CONFIG_DIR", null);
        }
    }
}
