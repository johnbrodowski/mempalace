using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MemPalace.Core;

public sealed class GeneralExtractorService
{
    // ── Compiled regex sets ───────────────────────────────────────────────────

    private static readonly Regex[] DecisionMarkers =
    [
        new(@"\bwe (decided|chose|picked|selected|went with|opted for)\b",  RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(decided|choosing) to\b",                                    RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bthe decision (is|was|to)\b",                                 RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\blet's go with\b",                                            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\binstead of\b.*\bwe\b",                                       RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static readonly Regex[] PreferenceMarkers =
    [
        new(@"\b(always|never) use\b",                                       RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bi (prefer|like|love|hate|dislike)\b",                        RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bwe (should always|should never|prefer|avoid)\b",             RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bthe (right|correct|best|proper) way\b",                      RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bdon't use\b",                                                RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static readonly Regex[] MilestoneMarkers =
    [
        new(@"\b(finally|successfully|managed to)\b",                        RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(breakthrough|milestone|shipped|launched|deployed|released)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bit (works|worked|is working)\b",                             RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bwe (finished|completed|done with|wrapped up)\b",             RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bgot it (working|running|fixed)\b",                           RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static readonly Regex[] ProblemMarkers =
    [
        new(@"\b(bug|crash|error|exception|issue|problem|failure)\b",        RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(broken|failing|doesn't work|not working|fails)\b",          RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bstuck (on|with|at)\b",                                       RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bcan't (figure out|get|make)\b",                              RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(segfault|stacktrace|traceback|panic)\b",                    RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static readonly Regex[] EmotionalMarkers =
    [
        new(@"\b(feel|feeling|felt)\b.*\b(happy|sad|frustrated|excited|anxious|proud|disappointed|overwhelmed)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(really hard|very difficult|super challenging)\b",           RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(love|hate) (this|working|doing|that)\b",                   RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(miss|missing)\b.*\b(you|them|home|family)\b",              RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(grateful|thankful|appreciate)\b",                           RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    // All categories in order – used for iteration
    private static readonly (string Type, Regex[] Markers)[] Categories =
    [
        ("decision",   DecisionMarkers),
        ("preference", PreferenceMarkers),
        ("milestone",  MilestoneMarkers),
        ("problem",    ProblemMarkers),
        ("emotional",  EmotionalMarkers),
    ];

    // Code-line detection helpers
    private static readonly Regex CodeStartKeyword =
        new(@"^(import |def |class |var |let |const |function )", RegexOptions.Compiled);

    private static readonly char[] CodeSymbols = ['=', '(', ')', '{', '}', ';'];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Extract structured memory chunks from <paramref name="text"/>.
    /// Only chunks whose best-matching category score &ge; <paramref name="minConfidence"/>
    /// are returned, and results are deduplicated by content.
    /// </summary>
    public IReadOnlyList<MemoryChunk> ExtractMemories(string text, double minConfidence = 0.3)
    {
        string prose = ExtractProse(text);
        IReadOnlyList<string> segments = SplitIntoSegments(prose);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<MemoryChunk>();

        foreach (string segment in segments)
        {
            string trimmed = segment.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (!seen.Add(trimmed)) continue;

            string bestType = string.Empty;
            double bestScore = 0.0;

            foreach (var (type, markers) in Categories)
            {
                var (score, _) = ScoreMarkers(trimmed, markers);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestType = type;
                }
            }

            if (bestScore >= minConfidence)
            {
                results.Add(new MemoryChunk
                {
                    Content    = trimmed,
                    MemoryType = bestType,
                });
            }
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Filter code lines out of <paramref name="text"/>, collapse multiple
    /// blank lines to one, and return the resulting prose.
    /// </summary>
    public string ExtractProse(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string[] lines = text.Split('\n');
        var kept = new List<string>();
        int consecutiveBlanks = 0;

        foreach (string line in lines)
        {
            if (IsCodeLine(line))
            {
                consecutiveBlanks = 0;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                consecutiveBlanks++;
                if (consecutiveBlanks <= 1) kept.Add(string.Empty);
            }
            else
            {
                consecutiveBlanks = 0;
                kept.Add(line);
            }
        }

        return string.Join('\n', kept).Trim();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private bool IsCodeLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;

        // Starts with indentation AND contains code symbols
        if ((line[0] == ' ' || line[0] == '\t') &&
            (line.IndexOfAny(CodeSymbols) >= 0 ||
             line.Contains("//", StringComparison.Ordinal) ||
             line.Contains('#')))
            return true;

        // Very low alpha ratio (numeric/symbol heavy)
        if (line.Length > 10)
        {
            int alphaCount = line.Count(char.IsLetter);
            double ratio = (double)alphaCount / line.Length;
            if (ratio < 0.4) return true;
        }

        // Starts with a code keyword
        if (CodeStartKeyword.IsMatch(line.TrimStart())) return true;

        return false;
    }

    private (double score, string[] keywords) ScoreMarkers(string text, Regex[] markers)
    {
        var matched = new List<string>();

        foreach (Regex rx in markers)
        {
            MatchCollection mc = rx.Matches(text);
            foreach (Match m in mc)
                matched.Add(m.Value);
        }

        int count = matched.Count;
        double score = count / (count + 2.0);
        return (score, matched.ToArray());
    }

    private static IReadOnlyList<string> SplitIntoSegments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        // Split on blank lines
        string[] paragraphs = text.Split(
            ["\r\n\r\n", "\n\n"],
            StringSplitOptions.RemoveEmptyEntries);

        var segments = new List<string>();

        foreach (string para in paragraphs)
        {
            string p = para.Trim();
            if (string.IsNullOrWhiteSpace(p)) continue;

            if (p.Length <= 500)
            {
                segments.Add(p);
            }
            else
            {
                // Further split on sentence boundaries
                string[] sentences = p.Split(". ", StringSplitOptions.RemoveEmptyEntries);
                var current = new StringBuilder();

                foreach (string sentence in sentences)
                {
                    if (current.Length > 0 && current.Length + sentence.Length + 2 > 500)
                    {
                        segments.Add(current.ToString().Trim());
                        current.Clear();
                    }
                    if (current.Length > 0) current.Append(". ");
                    current.Append(sentence);
                }

                if (current.Length > 0) segments.Add(current.ToString().Trim());
            }
        }

        return segments.AsReadOnly();
    }
}
