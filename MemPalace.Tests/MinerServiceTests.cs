using MemPalace.Core;
using Xunit;

namespace MemPalace.Tests;

/// <summary>
/// Mirrors Python tests/test_miner.py
/// Tests ScanProject gitignore handling and includeIgnored overrides.
/// </summary>
public sealed class MinerServiceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mempalace_miner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Repeat content to ensure files are non-trivial (mirrors Python * 20)
        File.WriteAllText(path, string.Concat(Enumerable.Repeat(content, 20)));
    }

    // Use a per-call unique DB path in the OS temp root (not inside the project tmpDir)
    // so that temp dir cleanup never races with SQLite's WAL file locks.
    private static MinerService BuildMiner()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            $"mempalace_miner_db_{Guid.NewGuid():N}.db");
        var cfg = new AppConfig
        {
            StorePath = Path.GetTempPath(),
            IndexPath = Path.GetTempPath(),
            DbPath    = dbPath,
        };
        return new MinerService(cfg, new DatabaseService(cfg));
    }

    private static List<string> ScannedFiles(
        string projectDir,
        bool respectGitignore = true,
        IReadOnlyList<string>? includeIgnored = null)
    {
        var miner = BuildMiner();
        var files = miner.ScanProject(projectDir, respectGitignore, includeIgnored);
        return files
            .Select(f => f.Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    // Mirrors: test_project_mining
    [Fact]
    public async Task ProjectMining_IndexesFilesIntoDatabase()
    {
        var projectDir = MakeTempDir();
        var dbPath = Path.Combine(Path.GetTempPath(), $"mempalace_mine_{Guid.NewGuid():N}.db");
        try
        {
            WriteFile(Path.Combine(projectDir, "backend", "app.py"), "def main():\n    print('hello world')\n");

            var cfg = new AppConfig
            {
                StorePath = projectDir,
                IndexPath = projectDir,
                DbPath    = dbPath,
            };
            var db    = new DatabaseService(cfg);
            var miner = new MinerService(cfg, db);

            await miner.MineAsync(projectDir, domain: "test_project");

            Assert.True(db.GetChunkCount() > 0);
        }
        finally
        {
            if (Directory.Exists(projectDir))
                Directory.Delete(projectDir, recursive: true);
            // db file in temp root — let OS clean up
        }
    }

    // Mirrors: test_scan_project_respects_gitignore
    [Fact]
    public void ScanProject_RespectsGitignore()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored.py\ngenerated/\n");
            WriteFile(Path.Combine(root, "src", "app.py"), "print('hello')\n");
            WriteFile(Path.Combine(root, "ignored.py"), "print('ignore me')\n");
            WriteFile(Path.Combine(root, "generated", "artifact.py"), "print('artifact')\n");

            Assert.Equal(new[] { "src/app.py" }, ScannedFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_respects_nested_gitignore
    [Fact]
    public void ScanProject_RespectsNestedGitignore()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            WriteFile(Path.Combine(root, "subrepo", ".gitignore"), "tasks/\n");
            WriteFile(Path.Combine(root, "subrepo", "src", "main.py"), "print('main')\n");
            WriteFile(Path.Combine(root, "subrepo", "tasks", "task.py"), "print('task')\n");
            WriteFile(Path.Combine(root, "subrepo", "debug.log"), "debug\n");

            Assert.Equal(new[] { "subrepo/src/main.py" }, ScannedFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_allows_nested_gitignore_override
    [Fact]
    public void ScanProject_AllowsNestedGitignoreOverride()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.csv\n");
            WriteFile(Path.Combine(root, "subrepo", ".gitignore"), "!keep.csv\n");
            WriteFile(Path.Combine(root, "drop.csv"), "a,b,c\n");
            WriteFile(Path.Combine(root, "subrepo", "keep.csv"), "a,b,c\n");

            Assert.Equal(new[] { "subrepo/keep.csv" }, ScannedFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_allows_gitignore_negation_when_parent_dir_is_visible
    [Fact]
    public void ScanProject_NegationWorksWhenParentDirIsVisible()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "generated/*\n!generated/keep.py\n");
            WriteFile(Path.Combine(root, "generated", "drop.py"), "print('drop')\n");
            WriteFile(Path.Combine(root, "generated", "keep.py"), "print('keep')\n");

            Assert.Equal(new[] { "generated/keep.py" }, ScannedFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_does_not_reinclude_file_from_ignored_directory
    [Fact]
    public void ScanProject_DoesNotReincludeFileFromIgnoredDir()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "generated/\n!generated/keep.py\n");
            WriteFile(Path.Combine(root, "generated", "drop.py"), "print('drop')\n");
            WriteFile(Path.Combine(root, "generated", "keep.py"), "print('keep')\n");

            Assert.Empty(ScannedFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_can_disable_gitignore
    [Fact]
    public void ScanProject_CanDisableGitignore()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "data/\n");
            WriteFile(Path.Combine(root, "data", "stuff.csv"), "a,b,c\n");

            Assert.Equal(new[] { "data/stuff.csv" }, ScannedFiles(root, respectGitignore: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_can_include_ignored_directory
    [Fact]
    public void ScanProject_CanIncludeIgnoredDirectory()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "docs/\n");
            WriteFile(Path.Combine(root, "docs", "guide.md"), "# Guide\n");

            Assert.Equal(new[] { "docs/guide.md" }, ScannedFiles(root, includeIgnored: ["docs"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_can_include_specific_ignored_file
    [Fact]
    public void ScanProject_CanIncludeSpecificIgnoredFile()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "generated/\n");
            WriteFile(Path.Combine(root, "generated", "drop.py"), "print('drop')\n");
            WriteFile(Path.Combine(root, "generated", "keep.py"), "print('keep')\n");

            Assert.Equal(new[] { "generated/keep.py" },
                ScannedFiles(root, includeIgnored: ["generated/keep.py"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_can_include_exact_file_without_known_extension
    [Fact]
    public void ScanProject_CanIncludeFileWithoutKnownExtension()
    {
        var root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "README\n");
            WriteFile(Path.Combine(root, "README"), "hello\n");

            Assert.Equal(new[] { "README" }, ScannedFiles(root, includeIgnored: ["README"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_include_override_beats_skip_dirs
    [Fact]
    public void ScanProject_IncludeIgnoredOverridesSkipDirs()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(Path.Combine(root, ".pytest_cache", "cache.py"), "print('cache')\n");

            Assert.Equal(new[] { ".pytest_cache/cache.py" },
                ScannedFiles(root, respectGitignore: false, includeIgnored: [".pytest_cache"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Mirrors: test_scan_project_skip_dirs_still_apply_without_override
    [Fact]
    public void ScanProject_SkipDirsApplyWithoutOverride()
    {
        var root = MakeTempDir();
        try
        {
            WriteFile(Path.Combine(root, ".pytest_cache", "cache.py"), "print('cache')\n");
            WriteFile(Path.Combine(root, "main.py"), "print('main')\n");

            Assert.Equal(new[] { "main.py" }, ScannedFiles(root, respectGitignore: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
