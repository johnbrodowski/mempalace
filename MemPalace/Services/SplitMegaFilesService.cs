using System.Text.RegularExpressions;

namespace MemPalace.Core;

public sealed class SplitMegaFilesService
{
    private static readonly HashSet<string> KnownStopwords = new(StringComparer.Ordinal)
    {
        "The", "A", "An", "I", "You", "He", "She", "We", "They",
        "This", "That", "Claude", "User", "Human", "Assistant"
    };

    private static readonly Regex TimestampPattern = new(
        @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}|\d{1,2}/\d{1,2}/\d{4}",
        RegexOptions.Compiled);

    private static readonly Regex CapitalizedWordPattern = new(
        @"\b([A-Z][a-z]{2,})\b",
        RegexOptions.Compiled);

    private static readonly Regex SlugCleanPattern = new(
        @"[^a-z0-9\s-]",
        RegexOptions.Compiled);

    public async Task<IReadOnlyList<string>> SplitDirectoryAsync(
        string sourceDir,
        string? outputDir = null,
        int minSessions = 2,
        bool dryRun = false)
    {
        var results = new List<string>();
        var extensions = new[] { "*.txt", "*.md", "*.jsonl" };

        foreach (var ext in extensions)
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, ext, SearchOption.TopDirectoryOnly))
            {
                var fileResults = await SplitFileAsync(file, outputDir, dryRun);
                if (fileResults.Count >= minSessions)
                    results.AddRange(fileResults);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<string>> SplitFileAsync(
        string filePath,
        string? outputDir = null,
        bool dryRun = false)
    {
        var lines = await File.ReadAllLinesAsync(filePath);
        var boundaries = FindSessionBoundaries(lines);

        if (boundaries.Count < 2)
            return Array.Empty<string>();

        var dir = outputDir ?? Path.GetDirectoryName(filePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var created = new List<string>();

        for (int i = 0; i < boundaries.Count; i++)
        {
            int startIdx = boundaries[i];
            int endIdx = (i + 1 < boundaries.Count) ? boundaries[i + 1] : lines.Length;

            var segmentLines = lines[startIdx..endIdx];

            var timestamp = ExtractTimestamp(segmentLines, 0);
            var people = ExtractPeople(segmentLines, 0, Math.Min(20, segmentLines.Length));
            var subject = ExtractSubject(segmentLines, 0, Math.Min(20, segmentLines.Length));
            var filename = MakeFilename(stem, timestamp, people, subject);
            var outPath = Path.Combine(dir, filename);

            created.Add(outPath);

            if (!dryRun)
            {
                Directory.CreateDirectory(dir);
                await File.WriteAllLinesAsync(outPath, segmentLines);
            }
        }

        return created;
    }

    private bool IsTrueSessionStart(string[] lines, int idx)
    {
        if (!lines[idx].Contains("Claude Code", StringComparison.OrdinalIgnoreCase))
            return false;

        int checkEnd = Math.Min(idx + 7, lines.Length);
        for (int j = idx + 1; j < checkEnd; j++)
        {
            var line = lines[j];
            if (line.Contains("Ctrl+E", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("previous messages", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("continuing from", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private IReadOnlyList<int> FindSessionBoundaries(string[] lines)
    {
        var boundaries = new List<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Claude Code", StringComparison.OrdinalIgnoreCase))
            {
                if (IsTrueSessionStart(lines, i))
                    boundaries.Add(i);
            }
        }

        return boundaries;
    }

    private string? ExtractTimestamp(string[] lines, int startIdx)
    {
        int checkEnd = Math.Min(startIdx + 10, lines.Length);
        for (int i = startIdx; i < checkEnd; i++)
        {
            var match = TimestampPattern.Match(lines[i]);
            if (match.Success)
                return NormalizeTimestamp(match.Value);
        }
        return null;
    }

    private static string NormalizeTimestamp(string raw)
    {
        // Attempt to parse and reformat as YYYY-MM-DD
        // Handle ISO-like: 2024-01-15T10:30 or 2024-01-15 10:30
        var isoMatch = Regex.Match(raw, @"(\d{4})-(\d{2})-(\d{2})");
        if (isoMatch.Success)
            return $"{isoMatch.Groups[1].Value}-{isoMatch.Groups[2].Value}-{isoMatch.Groups[3].Value}";

        // Handle M/D/YYYY
        var usMatch = Regex.Match(raw, @"(\d{1,2})/(\d{1,2})/(\d{4})");
        if (usMatch.Success)
        {
            var month = int.Parse(usMatch.Groups[1].Value);
            var day = int.Parse(usMatch.Groups[2].Value);
            var year = int.Parse(usMatch.Groups[3].Value);
            return $"{year:D4}-{month:D2}-{day:D2}";
        }

        return raw;
    }

    private IReadOnlyList<string> ExtractPeople(string[] lines, int startIdx, int endIdx)
    {
        var people = new List<string>();
        var seen = new HashSet<string>();

        for (int i = startIdx; i < endIdx && i < lines.Length; i++)
        {
            foreach (Match m in CapitalizedWordPattern.Matches(lines[i]))
            {
                var word = m.Groups[1].Value;
                if (!KnownStopwords.Contains(word) && seen.Add(word))
                    people.Add(word);
            }
        }

        return people;
    }

    private string ExtractSubject(string[] lines, int startIdx, int endIdx)
    {
        for (int i = startIdx + 1; i < endIdx && i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length > 3 &&
                !line.StartsWith("#", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(line))
            {
                var subject = line.Length > 30 ? line[..30] : line;
                return subject;
            }
        }
        return "session";
    }

    public string MakeFilename(string stem, string? timestamp, IEnumerable<string> people, string subject)
    {
        var ts = timestamp ?? "unknown";

        var peopleList = people.Take(2).ToList();
        var peoplePart = peopleList.Count > 0
            ? string.Join("-", peopleList)
            : "general";

        // Slugify subject: lowercase, spaces to hyphens, alphanumeric + hyphens only, max 20 chars
        var slug = subject.ToLowerInvariant();
        slug = slug.Replace(' ', '-');
        slug = SlugCleanPattern.Replace(slug, string.Empty);
        // Collapse multiple hyphens
        slug = Regex.Replace(slug, @"-{2,}", "-").Trim('-');
        if (slug.Length > 20) slug = slug[..20].TrimEnd('-');
        if (string.IsNullOrEmpty(slug)) slug = "session";

        return $"{stem}__{ts}_{peoplePart}_{slug}.txt";
    }
}
