using System.Text.RegularExpressions;

namespace MemPalace.Core;

public static class EntityDetectorService
{
    // ── Stop-word filter ──────────────────────────────────────────────────────

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        // Determiners / pronouns / conjunctions
        "The", "This", "That", "These", "Those", "There", "Their", "They",
        "When", "Where", "What", "Which", "Who", "Why", "How",
        "And", "But", "For", "Not", "All", "Any", "Each", "Few",
        "More", "Most", "Other", "Some", "Such",
        // Prepositions / connectors
        "Into", "Over", "After", "Before", "Since", "Just", "Only", "Also",
        "Very", "Well", "Even", "Still", "While", "Then", "Than",
        "With", "From", "About", "Through", "During", "Including", "Until",
        "Against", "Among", "Throughout", "Despite", "Towards", "Upon",
        // Days of the week
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        // Months
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
        // Type / value keywords
        "True", "False", "None", "Null", "String", "Integer", "Boolean",
        "Object", "Array", "List",
        // Document structure
        "Section", "Chapter", "Table", "Figure", "Note", "Example",
        "Version", "Update", "New", "Old",
        "First", "Second", "Third", "Last", "Next", "Previous", "Current", "Default",
        // Extra common English words that start with a capital in sentences
        "He", "She", "It", "We", "You", "I", "A", "An",
        "If", "Or", "So", "As", "At", "By", "Do", "Go", "In", "Is",
        "Be", "Up", "No", "On", "To", "Of", "Our", "Its", "His", "Her",
        "Was", "Has", "Had", "Did", "Are", "Can", "May", "Let", "Get",
        "Got", "Use", "See", "Put", "Set", "Try", "Run", "Add",
        "However", "Therefore", "Although", "Because", "Whether", "Between",
        "Without", "Within", "Along", "Below", "Above",
    };

    // ── File extension lists ───────────────────────────────────────────────────

    private static readonly HashSet<string> ProseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".rst", ".csv"
    };

    // ── Directories to skip while walking ────────────────────────────────────

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", "bin", "obj"
    };

    // ── Compiled regexes ──────────────────────────────────────────────────────

    private static readonly Regex SingleWord =
        new(@"\b([A-Z][a-z]{1,19})\b", RegexOptions.Compiled);

    private static readonly Regex MultiWord =
        new(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Walk <paramref name="projectDir"/> for prose files, then classify every
    /// candidate name found in their combined text.
    /// </summary>
    public static DetectedEntities DetectEntities(IEnumerable<string> filePaths, int maxFiles = 10)
    {
        var prose = filePaths
            .Where(p => ProseExtensions.Contains(Path.GetExtension(p)))
            .Take(maxFiles)
            .ToArray();

        if (prose.Length == 0)
            return new DetectedEntities();

        var combined = string.Join("\n", prose.Select(p =>
        {
            try { return File.ReadAllText(p); }
            catch { return string.Empty; }
        }));

        var candidates = ExtractCandidates(combined);
        if (candidates.Count == 0)
            return new DetectedEntities();

        var lines = combined.Split('\n');

        var people    = new List<string>();
        var projects  = new List<string>();
        var uncertain = new List<string>();

        foreach (var (name, count) in candidates)
        {
            string classification = ClassifyEntity(name, count, combined, lines);
            switch (classification)
            {
                case "person":  people.Add(name);    break;
                case "project": projects.Add(name);  break;
                default:        uncertain.Add(name); break;
            }
        }

        return new DetectedEntities
        {
            People    = people.AsReadOnly(),
            Projects  = projects.AsReadOnly(),
            Uncertain = uncertain.AsReadOnly(),
        };
    }

    /// <summary>
    /// Extract title-case candidate names from <paramref name="text"/> that
    /// appear at least 3 times and are not stop-words.
    /// </summary>
    public static Dictionary<string, int> ExtractCandidates(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        // Multi-word first so they are counted as units before single-word pass
        foreach (Match m in MultiWord.Matches(text))
        {
            string name = m.Groups[1].Value;
            if (Stopwords.Contains(name)) continue;
            counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
        }

        // Single-word pass — skip names already captured via multi-word
        foreach (Match m in SingleWord.Matches(text))
        {
            string name = m.Groups[1].Value;
            if (Stopwords.Contains(name)) continue;
            // Do not double-count words that were part of multi-word candidates
            bool inMulti = counts.Keys.Any(k => k.Contains(' ') &&
                k.Split(' ').Contains(name, StringComparer.Ordinal));
            if (inMulti) continue;
            counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
        }

        // Keep only candidates that appear 3+ times
        return counts
            .Where(kv => kv.Value >= 3)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Two-pass scoring: accumulate person signals and project signals, then
    /// apply classification rules.
    /// </summary>
    public static string ClassifyEntity(string name, int frequency, string text, string[] lines)
    {
        // ── Person signals ────────────────────────────────────────────────────
        int personScore = 0;
        int personSignalTypes = 0;

        // Dialogue markers  (+3 each)
        string[] dialoguePatterns =
        [
            $@"\b{Regex.Escape(name)} said\b",
            $@"\b{Regex.Escape(name)} asked\b",
            $@"\b{Regex.Escape(name)} told\b",
            $@"\b{Regex.Escape(name)} replied\b",
        ];
        int dialogueHits = dialoguePatterns
            .Sum(p => Regex.Matches(text, p, RegexOptions.IgnoreCase).Count);
        if (dialogueHits > 0)
        {
            personScore += dialogueHits * 3;
            personSignalTypes++;
        }

        // Person verbs  (+2 each)
        var verbMatch = Regex.Matches(text,
            $@"\b{Regex.Escape(name)} (went|came|got|had|was|is|has|did|made|felt)\b",
            RegexOptions.IgnoreCase);
        if (verbMatch.Count > 0)
        {
            personScore += verbMatch.Count * 2;
            personSignalTypes++;
        }

        // Pronouns within 50 chars  (+2 each, max 6)
        string[] pronouns = ["he", "she", "him", "her", "his", "hers", "they", "them"];
        int pronounHits = 0;
        int nameIdx = 0;
        while (nameIdx < text.Length && pronounHits < 3)
        {
            int pos = text.IndexOf(name, nameIdx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) break;
            int start = Math.Max(0, pos - 50);
            int end   = Math.Min(text.Length, pos + name.Length + 50);
            string window = text[start..end].ToLowerInvariant();
            foreach (string pronoun in pronouns)
            {
                if (window.Contains(pronoun, StringComparison.Ordinal))
                    pronounHits++;
            }
            nameIdx = pos + 1;
        }
        int pronounScore = Math.Min(pronounHits * 2, 6);
        if (pronounScore > 0)
        {
            personScore += pronounScore;
            personSignalTypes++;
        }

        // Direct address  (+4 each)
        string[] addressPatterns =
        [
            $@"\bhi {Regex.Escape(name)}\b",
            $@"\bhey {Regex.Escape(name)}\b",
            $@"\bdear {Regex.Escape(name)}\b",
        ];
        int addressHits = addressPatterns
            .Sum(p => Regex.Matches(text, p, RegexOptions.IgnoreCase).Count);
        if (addressHits > 0)
        {
            personScore += addressHits * 4;
            personSignalTypes++;
        }

        // ── Project signals ───────────────────────────────────────────────────
        int projectScore = 0;

        // Versioned  (+3)
        var versionedMatches = Regex.Matches(text,
            $@"\b{Regex.Escape(name)} (version|v[0-9]|v [0-9])\b",
            RegexOptions.IgnoreCase);
        projectScore += versionedMatches.Count * 3;

        // Code usage  (+2 each)
        var codeUsageMatches = Regex.Matches(text,
            $@"\b(using|import|require|install)\s+{Regex.Escape(name)}\b",
            RegexOptions.IgnoreCase);
        projectScore += codeUsageMatches.Count * 2;

        // File references  (+3 each)
        var fileRefMatches = Regex.Matches(text,
            $@"{Regex.Escape(name)}\.(py|js|ts|rs|go|java|cs|cpp|rb|sh)\b",
            RegexOptions.IgnoreCase);
        projectScore += fileRefMatches.Count * 3;

        // Project nouns  (+2 each)
        var projectNounMatches = Regex.Matches(text,
            $@"\b(project|repo|library|framework|package|tool|app)\s+{Regex.Escape(name)}\b",
            RegexOptions.IgnoreCase);
        projectScore += projectNounMatches.Count * 2;

        // ── Classification rules ──────────────────────────────────────────────
        if (personScore >= 4 && personSignalTypes >= 2)
            return "person";

        if (projectScore >= 4)
            return "project";

        if (personScore >= 2 && frequency >= 5)
            return "person";

        return "uncertain";
    }

    /// <summary>
    /// Recursively enumerate prose files under <paramref name="projectDir"/>,
    /// skipping common non-source directories, up to <paramref name="maxFiles"/>.
    /// </summary>
    public static IReadOnlyList<string> ScanForDetection(string projectDir, int maxFiles = 10)
    {
        var results = new List<string>();

        if (!Directory.Exists(projectDir))
            return results;

        var queue = new Queue<string>();
        queue.Enqueue(projectDir);

        while (queue.Count > 0 && results.Count < maxFiles)
        {
            string dir = queue.Dequeue();

            // Enqueue sub-directories (skip blacklisted names)
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    string dirName = Path.GetFileName(sub);
                    if (!SkipDirs.Contains(dirName))
                        queue.Enqueue(sub);
                }
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible dirs */ }

            // Collect matching files
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (ProseExtensions.Contains(Path.GetExtension(file)))
                    {
                        results.Add(file);
                        if (results.Count >= maxFiles)
                            break;
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible files */ }
        }

        return results.AsReadOnly();
    }
}
