using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MemPalace.Core;

public sealed class MinerService
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", "bin", "obj", ".vs", "dist", "build", ".next", "vendor",
        ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".eggs", "*.egg-info",
        ".idea", ".vscode", "coverage", ".coverage",
    };

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico",
        ".woff", ".woff2", ".ttf", ".eot", ".zip", ".tar", ".gz", ".pdf",
        ".db", ".sqlite", ".sqlite3"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".rst", ".py", ".cs", ".js", ".ts", ".jsx", ".tsx",
        ".java", ".go", ".rs", ".cpp", ".c", ".h", ".rb", ".php", ".html",
        ".css", ".scss", ".json", ".yaml", ".yml", ".toml", ".xml",
        ".sh", ".bash", ".ps1", ".sql", ".csv", ".tsv", ".log", ".ini", ".cfg", ".env"
    };

    private readonly AppConfig _config;
    private readonly DatabaseService _db;
    private readonly EmbeddingService? _embedder;

    public MinerService(AppConfig config, DatabaseService db, EmbeddingService? embedder = null)
    {
        _config   = config;
        _db       = db;
        _embedder = embedder;
    }

    public async Task<MineResult> MineAsync(
        string path,
        string mode = "projects",
        string? domain = null,
        int limit = 0,
        bool dryRun = false)
    {
        var result = new MineResult
        {
            TopicCounts = new Dictionary<string, int>()
        };

        if (mode != "projects")
            return result;

        string effectiveDomain = domain ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

        var files = ScanProject(path);

        int filesScanned = 0;
        int chunksAdded = 0;
        int filesSkipped = 0;
        var topicCounts = new Dictionary<string, int>();

        foreach (var relPath in files)
        {
            if (limit > 0 && filesScanned >= limit)
                break;

            string fullPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(path, relPath);

            if (_db.ChunkExists(relPath))
            {
                filesSkipped++;
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(fullPath);
            }
            catch
            {
                filesSkipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                filesSkipped++;
                continue;
            }

            filesScanned++;

            var chunks = ChunkText(content);
            foreach (var (chunkContent, chunkIndex) in chunks)
            {
                string topic = RoomDetectorService.DetectTopic(chunkContent);
                string category = RoomDetectorService.DetectCategory(chunkContent);

                string idSource = relPath + chunkIndex.ToString();
                string id = ComputeSha256Hex(idSource);

                var doc = new ChunkRecord
                {
                    Id = id,
                    Domain = effectiveDomain,
                    Topic = topic,
                    Category = category,
                    SourceFile = relPath,
                    Content = chunkContent,
                    ChunkIndex = chunkIndex,
                    AddedBy = "mempalace-miner",
                    FiledAt = DateTime.UtcNow.ToString("o")
                };

                if (!dryRun)
                {
                    _db.AddChunk(doc);

                    // Best-effort embedding — never fails the mine operation
                    if (_embedder is not null)
                    {
                        try
                        {
                            var vec = _embedder.Embed(chunkContent);
                            _db.UpsertEmbedding(id, vec);
                        }
                        catch { /* embedding is enhancement-only */ }
                    }
                }

                chunksAdded++;
                topicCounts[topic] = topicCounts.GetValueOrDefault(topic) + 1;
            }
        }

        return new MineResult
        {
            FilesScanned = filesScanned,
            ChunksAdded = chunksAdded,
            FilesSkipped = filesSkipped,
            TopicCounts = topicCounts
        };
    }

    public IReadOnlyList<string> ScanProject(
        string projectDir,
        bool respectGitignore = true,
        IReadOnlyList<string>? includeIgnored = null)
    {
        var results = new List<string>();

        // Pre-process includeIgnored entries into three sets:
        //   bypassAllDirs   – enter these dirs (bypass SkipDirs+gitignore) and include ALL files inside
        //   bypassSpecific  – enter these dirs (bypass SkipDirs+gitignore) but only include forcedFiles
        //   forcedFiles     – specific relative paths (or root filenames) that are always included
        var bypassAllDirs  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bypassSpecific = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forcedFiles    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawEntry in includeIgnored ?? [])
        {
            var entry = rawEntry.Replace('\\', '/').Trim('/');
            if (entry.Contains('/'))
            {
                // e.g. "generated/keep.py" – bypass the leading dir, force a specific file
                bypassSpecific.Add(entry.Split('/')[0]);
                forcedFiles.Add(entry);
            }
            else
            {
                // e.g. "docs", ".pytest_cache", "README" – treat as both a bypass-all dir
                // and a forced root filename (handles files without known extensions)
                bypassAllDirs.Add(entry);
                forcedFiles.Add(entry);
            }
        }

        // Load root .gitignore if present
        GitignoreMatcher? rootMatcher = null;
        if (respectGitignore)
        {
            string rootGitignore = Path.Combine(projectDir, ".gitignore");
            if (File.Exists(rootGitignore))
                rootMatcher = new GitignoreMatcher(rootGitignore);
        }

        // Build the initial matcher stack (root gitignore, if any)
        var rootStack = new List<GitignoreMatcher>();
        if (rootMatcher is not null) rootStack.Add(rootMatcher);

        WalkDirectory(
            projectDir, projectDir, rootStack, respectGitignore, results,
            bypassAllDirs, bypassSpecific, forcedFiles,
            isInAllIncludedDir: false, isInSpecificDir: false);
        return results;
    }

    private static void WalkDirectory(
        string rootDir,
        string currentDir,
        List<GitignoreMatcher> matcherStack,
        bool respectGitignore,
        List<string> results,
        HashSet<string> bypassAllDirs,
        HashSet<string> bypassSpecific,
        HashSet<string> forcedFiles,
        bool isInAllIncludedDir,
        bool isInSpecificDir)
    {
        // Stack a local .gitignore on top of the inherited stack (not for root — already loaded)
        var localStack = matcherStack;
        if (respectGitignore && currentDir != rootDir)
        {
            string localGitignore = Path.Combine(currentDir, ".gitignore");
            if (File.Exists(localGitignore))
            {
                localStack = new List<GitignoreMatcher>(matcherStack)
                {
                    new GitignoreMatcher(localGitignore)
                };
            }
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(currentDir);
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry);

            if (Directory.Exists(entry))
            {
                bool isBypassAll      = bypassAllDirs.Contains(name);
                bool isBypassSpecific = bypassSpecific.Contains(name);
                bool shouldBypassDir  = isBypassAll || isBypassSpecific || isInAllIncludedDir;

                // Apply SkipDirs – overridden by explicit bypass
                if (!shouldBypassDir && SkipDirs.Contains(name))
                    continue;

                string relDir = Path.GetRelativePath(rootDir, entry).Replace('\\', '/');

                // Apply gitignore – overridden by explicit bypass or already being inside a forced dir
                if (!shouldBypassDir && respectGitignore && IsIgnoredByStack(localStack, relDir + "/"))
                    continue;

                // Determine flags for recursive descent
                bool nextIsInAll      = isInAllIncludedDir || isBypassAll;
                bool nextIsInSpecific = !nextIsInAll && (isBypassSpecific || isInSpecificDir);

                WalkDirectory(
                    rootDir, entry, localStack, respectGitignore, results,
                    bypassAllDirs, bypassSpecific, forcedFiles,
                    nextIsInAll, nextIsInSpecific);
            }
            else
            {
                string relFile = Path.GetRelativePath(rootDir, entry).Replace('\\', '/');
                bool isForced = forcedFiles.Contains(relFile)  // exact relative path match
                             || forcedFiles.Contains(name)     // root filename match (e.g. "README")
                             || isInAllIncludedDir;            // inside a fully-bypassed dir

                if (isForced)
                {
                    results.Add(relFile);
                    continue;
                }

                // Inside a specific-file-only bypass dir: only forced files are allowed
                if (isInSpecificDir)
                    continue;

                string ext = Path.GetExtension(entry);
                if (SkipExtensions.Contains(ext))
                    continue;
                if (!TextExtensions.Contains(ext))
                    continue;

                if (respectGitignore && IsIgnoredByStack(localStack, relFile))
                    continue;

                results.Add(relFile);
            }
        }
    }

    public static IReadOnlyList<(string content, int chunkIndex)> ChunkText(
        string content,
        int chunkSize = 800,
        int overlap = 100)
    {
        var chunks = new List<(string content, int chunkIndex)>();
        if (string.IsNullOrEmpty(content))
            return chunks;

        // Split into paragraph-level segments first
        var paragraphs = SplitIntoParagraphs(content);

        var segments = new List<string>();
        foreach (var para in paragraphs)
        {
            if (para.Length <= chunkSize)
            {
                segments.Add(para);
            }
            else
            {
                // Try sentence boundaries
                var sentences = SplitIntoSentences(para);
                foreach (var sentence in sentences)
                {
                    if (sentence.Length <= chunkSize)
                    {
                        segments.Add(sentence);
                    }
                    else
                    {
                        // Hard split
                        int pos = 0;
                        while (pos < sentence.Length)
                        {
                            segments.Add(sentence.Substring(pos, Math.Min(chunkSize, sentence.Length - pos)));
                            pos += chunkSize;
                        }
                    }
                }
            }
        }

        // Accumulate segments into chunks with overlap
        var currentChunk = new StringBuilder();
        int chunkIndex = 0;

        foreach (var seg in segments)
        {
            // If adding this segment would overflow, flush current chunk
            if (currentChunk.Length > 0 && currentChunk.Length + 1 + seg.Length > chunkSize)
            {
                string chunkText = currentChunk.ToString().Trim();
                if (chunkText.Length > 0)
                {
                    chunks.Add((chunkText, chunkIndex++));
                }

                // Start new chunk with overlap from the end of the previous chunk
                string tail = chunkText.Length > overlap ? chunkText[^overlap..] : chunkText;
                currentChunk.Clear();
                currentChunk.Append(tail);
                if (currentChunk.Length > 0)
                    currentChunk.Append(' ');
            }

            if (currentChunk.Length > 0 && currentChunk[^1] != ' ')
                currentChunk.Append(' ');
            currentChunk.Append(seg);
        }

        // Flush any remaining content
        if (currentChunk.Length > 0)
        {
            string chunkText = currentChunk.ToString().Trim();
            if (chunkText.Length > 0)
                chunks.Add((chunkText, chunkIndex));
        }

        return chunks;
    }

    private static List<string> SplitIntoParagraphs(string text)
    {
        var parts = Regex.Split(text, @"\n\n+");
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }
        return result;
    }

    private static List<string> SplitIntoSentences(string text)
    {
        // Split on ". ", "! ", "? " but keep the punctuation attached
        var parts = Regex.Split(text, @"(?<=[.!?])\s+");
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }
        return result;
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Apply each matcher in the stack in order (parent first).
    /// Later matchers can negate rules established by earlier ones.
    /// </summary>
    private static bool IsIgnoredByStack(IReadOnlyList<GitignoreMatcher> stack, string relativePath)
    {
        bool ignored = false;
        foreach (var m in stack)
            ignored = m.ApplyRules(ignored, relativePath);
        return ignored;
    }
}

public sealed class GitignoreMatcher
{
    private readonly List<(Regex pattern, bool negated)> _rules = new();

    public GitignoreMatcher(string gitignorePath)
    {
        if (!File.Exists(gitignorePath))
            return;

        foreach (var rawLine in File.ReadAllLines(gitignorePath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            bool negated = line.StartsWith('!');
            if (negated)
                line = line[1..].Trim();

            bool anchored = line.StartsWith('/');
            if (anchored)
                line = line[1..];

            var regex = GlobToRegex(line, anchored);
            _rules.Add((regex, negated));
        }
    }

    /// <summary>Apply this matcher's rules, starting from an inherited <paramref name="currentIgnored"/> state.</summary>
    public bool ApplyRules(bool currentIgnored, string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        bool ignored = currentIgnored;
        foreach (var (pattern, negated) in _rules)
        {
            if (pattern.IsMatch(path))
                ignored = !negated;
        }
        return ignored;
    }

    public bool IsIgnored(string relativePath) => ApplyRules(false, relativePath);

    private static Regex GlobToRegex(string glob, bool anchored)
    {
        var sb = new StringBuilder();

        // If anchored, match from start; otherwise allow any path prefix
        if (anchored)
            sb.Append('^');
        else
            sb.Append("(^|.*/)");

        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];

            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // ** matches any path segment sequence
                sb.Append(".*");
                i += 2;
                // Skip a trailing slash after **
                if (i < glob.Length && glob[i] == '/')
                    i++;
            }
            else if (c == '*')
            {
                // * matches one or more characters that aren't / (not zero, so "dir/*" doesn't match "dir/" itself)
                sb.Append("[^/]+");
                i++;
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        // If pattern doesn't end with /, match both files and directories
        if (!glob.EndsWith('/'))
            sb.Append("(/.*)?$");
        else
            sb.Append('$');

        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

public sealed class MineResult
{
    public int FilesScanned { get; init; }
    public int ChunksAdded { get; init; }
    public int FilesSkipped { get; init; }
    public Dictionary<string, int> TopicCounts { get; init; } = new();
}
