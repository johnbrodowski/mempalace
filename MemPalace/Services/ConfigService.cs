using System.Text.Json;
using MemPalace.Core;

namespace MemPalace.Services;

public static class ConfigService
{
    private static string ConfigFile =>
        Path.Combine(
            Environment.GetEnvironmentVariable("MEMPALACE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mempalace"),
            "config.json");

    public static AppConfig LoadOrCreate()
    {
        if (File.Exists(ConfigFile))
        {
            var json = File.ReadAllText(ConfigFile);
            var existing = JsonSerializer.Deserialize<AppConfig>(json);
            if (existing is not null)
            {
                return existing;
            }
        }

        var config = new AppConfig();
        Save(config);
        return config;
    }

    public static async Task InitializeStoreAsync(AppConfig config, string targetRoot)
    {
        Directory.CreateDirectory(config.StorePath);
        Directory.CreateDirectory(config.IndexPath);
        Directory.CreateDirectory(Path.Combine(targetRoot, ".mempalace"));

        var readme = Path.Combine(config.StorePath, "README.txt");
        await File.WriteAllTextAsync(readme, "MemPalace store initialized.");
        Save(config);
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
