using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace MemPalace.Core;

public sealed class DialectService
{
    // -------------------------------------------------------------------------
    // Static tables
    // -------------------------------------------------------------------------

    private static readonly Dictionary<string, string> EmotionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vulnerability"]  = "vul",
        ["joy"]            = "joy",
        ["fear"]           = "fea",
        ["anger"]          = "ang",
        ["sadness"]        = "sad",
        ["disgust"]        = "dsg",
        ["surprise"]       = "sur",
        ["anticipation"]   = "ant",
        ["trust"]          = "tru",
        ["love"]           = "lov",
        ["excitement"]     = "exc",
        ["anxiety"]        = "anx",
        ["frustration"]    = "fru",
        ["relief"]         = "rel",
        ["pride"]          = "prd",
        ["shame"]          = "shm",
        ["guilt"]          = "glt",
        ["envy"]           = "env",
        ["gratitude"]      = "grt",
        ["hope"]           = "hop",
        ["nostalgia"]      = "nos",
        ["loneliness"]     = "lon",
        ["confusion"]      = "cnf",
        ["determination"]  = "det",
        ["curiosity"]      = "cur",
        ["regret"]         = "rgt",
        ["empathy"]        = "emp",
        ["overwhelm"]      = "ovw",
        ["boredom"]        = "bor",
        ["awe"]            = "awe",
    };

    // Reverse lookup: code → name
    private static readonly Dictionary<string, string> EmotionNames =
        EmotionCodes.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> FlagSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ORIGIN"]    = ["first time", "originally", "started", "beginning", "genesis", "founded"],
        ["CORE"]      = ["always", "fundamental", "core", "essential", "critical", "central", "key"],
        ["SENSITIVE"] = ["private", "personal", "sensitive", "confidential", "secret", "don't share"],
        ["PIVOT"]     = ["changed", "pivot", "shift", "transition", "turning point", "switched"],
        ["GENESIS"]   = ["created", "built", "launched", "started", "initiated", "began"],
        ["DECISION"]  = ["decided", "chose", "selected", "picked", "went with", "opted"],
        ["TECHNICAL"] = ["code", "function", "api", "database", "server", "deploy", "bug"],
    };

    private static readonly Dictionary<string, string[]> EmotionSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["joy"]           = ["happy", "excited", "great", "wonderful", "amazing", "fantastic", "love"],
        ["sadness"]       = ["sad", "miss", "lost", "grief", "sorry", "unfortunately", "regret"],
        ["fear"]          = ["afraid", "scared", "worried", "anxious", "nervous", "panic", "fear"],
        ["anger"]         = ["angry", "frustrated", "annoyed", "irritated", "furious", "upset"],
        ["vulnerability"] = ["vulnerable", "exposed", "open", "honest", "admit", "struggle"],
        ["determination"] = ["determined", "committed", "will", "must", "need to", "going to"],
        ["pride"]         = ["proud", "accomplished", "achieved", "success", "nailed", "pulled off"],
        ["hope"]          = ["hope", "optimistic", "looking forward", "hopefully", "believe"],
        ["overwhelm"]     = ["overwhelmed", "too much", "can't handle", "swamped", "drowning"],
        ["gratitude"]     = ["grateful", "thankful", "appreciate", "thanks", "thank you"],
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by",
        "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
        "will", "would", "could", "should", "may", "might", "shall", "can",
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
        "my", "your", "his", "its", "our", "their", "this", "that", "these", "those",
        "not", "no", "nor", "so", "yet", "as", "if", "then", "than", "because", "while",
        "about", "after", "before", "between", "during", "from", "into", "through", "under", "until",
    };

    private static readonly string[] DecisionWords =
        ["decided", "chose", "because", "so that", "in order to"];

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------

    private readonly Dictionary<string, string> _entityCodes;  // name → 3-letter code
    private readonly HashSet<string> _skipNames;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public DialectService(Dictionary<string, string>? entities = null, IEnumerable<string>? skipNames = null)
    {
        _entityCodes = entities is not null
            ? new Dictionary<string, string>(entities, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _skipNames = skipNames is not null
            ? new HashSet<string>(skipNames, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    public static DialectService FromConfig(string configPath)
    {
        var json    = File.ReadAllText(configPath);
        var doc     = JsonDocument.Parse(json).RootElement;
        var entities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skip     = new List<string>();

        if (doc.TryGetProperty("entities", out var entEl))
            foreach (var prop in entEl.EnumerateObject())
                entities[prop.Name] = prop.Value.GetString() ?? "";

        if (doc.TryGetProperty("skip", out var skipEl))
            foreach (var item in skipEl.EnumerateArray())
            {
                var s = item.GetString();
                if (s is not null) skip.Add(s);
            }

        return new DialectService(entities, skip);
    }

    public void SaveConfig(string configPath)
    {
        var obj = new
        {
            entities = _entityCodes,
            skip     = _skipNames.ToArray(),
        };
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    // -------------------------------------------------------------------------
    // Core operations
    // -------------------------------------------------------------------------

    public string? EncodeEntity(string name) =>
        _entityCodes.TryGetValue(name, out var code) ? code : null;

    public string EncodeEmotions(IEnumerable<string> emotions)
    {
        var codes = emotions
            .Select(e => EmotionCodes.TryGetValue(e, out var c) ? c : e)
            .ToList();
        return string.Join("+", codes);
    }

    public string Compress(string text, Dictionary<string, string>? metadata = null)
    {
        var lower = text.ToLowerInvariant();

        // ---- 1. Entity substitution ----
        var working = text;
        foreach (var (name, code) in _entityCodes)
        {
            if (_skipNames.Contains(name)) continue;
            // Replace whole-word occurrences (case-insensitive)
            working = Regex.Replace(working, $@"\b{Regex.Escape(name)}\b", code,
                RegexOptions.IgnoreCase);
        }

        // ---- 2. Topic extraction ----
        var wordFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in Regex.Split(lower, @"\W+"))
        {
            if (word.Length <= 3 || StopWords.Contains(word)) continue;
            wordFreq[word] = wordFreq.GetValueOrDefault(word) + 1;
        }
        // Boost capitalized words (in original text)
        foreach (var word in Regex.Split(text, @"\W+"))
        {
            if (word.Length > 3 && char.IsUpper(word[0]) && wordFreq.ContainsKey(word))
                wordFreq[word] += 2;
        }
        var topics = wordFreq
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();
        var topicsStr = string.Join("+", topics);

        // ---- 3. Key sentence ----
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
        string keySentence = "";
        int bestScore = -1;
        foreach (var sentence in sentences)
        {
            var sLower = sentence.ToLowerInvariant();
            var score  = DecisionWords.Count(w => sLower.Contains(w));
            if (score > bestScore)
            {
                bestScore    = score;
                keySentence  = sentence;
            }
        }
        // Fallback to first sentence if none scored
        if (string.IsNullOrWhiteSpace(keySentence) && sentences.Length > 0)
            keySentence = sentences[0];
        keySentence = keySentence.Trim();
        if (keySentence.Length > 55)
            keySentence = keySentence[..55] + "…";

        // ---- 4. Emotion detection ----
        var detectedEmotions = new List<string>();
        foreach (var (emotion, signals) in EmotionSignals)
        {
            if (signals.Any(s => lower.Contains(s)))
                detectedEmotions.Add(emotion);
        }
        var emotionsStr = EncodeEmotions(detectedEmotions);

        // ---- 5. Flag detection ----
        var detectedFlags = new List<string>();
        foreach (var (flag, signals) in FlagSignals)
        {
            if (signals.Any(s => lower.Contains(s)))
                detectedFlags.Add(flag);
        }
        var flagsStr = string.Join("+", detectedFlags);

        // ---- 6. Assemble output ----
        var parts = new StringBuilder();

        // Prepend metadata comment lines
        if (metadata is not null)
            foreach (var (k, v) in metadata)
                parts.AppendLine($"# {k}: {v}");

        parts.Append($"{topicsStr}|{keySentence}|{emotionsStr}|{flagsStr}");
        return parts.ToString();
    }

    public string Decode(string dialectText)
    {
        // Strip comment lines
        var lines = dialectText
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#'))
            .ToList();
        var body = string.Join("\n", lines).Trim();

        var parts = body.Split('|', 4);
        var topicsPart   = parts.Length > 0 ? parts[0].Trim() : "";
        var sentencePart = parts.Length > 1 ? parts[1].Trim() : "";
        var emotionsPart = parts.Length > 2 ? parts[2].Trim() : "";
        var flagsPart    = parts.Length > 3 ? parts[3].Trim() : "";

        // Reverse emotion codes → names
        if (!string.IsNullOrEmpty(emotionsPart))
        {
            var decoded = emotionsPart
                .Split('+')
                .Select(code => EmotionNames.TryGetValue(code.Trim(), out var name) ? name : code.Trim());
            emotionsPart = string.Join(", ", decoded);
        }

        return $"Topics: {topicsPart}\nSentence: {sentencePart}\nEmotions: {emotionsPart}\nFlags: {flagsPart}";
    }

    public static int CountTokens(string text) => text.Length / 3;

    // ── Dialect specification ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the full AAAK dialect specification as a human-readable string,
    /// suitable for embedding in an MCP tool response or system prompt.
    /// </summary>
    public string GetSpec()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AAAK Compression Dialect Specification");
        sb.AppendLine();
        sb.AppendLine("## Output Format");
        sb.AppendLine("  topics|key_sentence|emotion_codes|flag_codes");
        sb.AppendLine("  # comment lines may precede the body");
        sb.AppendLine();
        sb.AppendLine("## Emotion Codes  (name → 3-letter code)");
        foreach (var (name, code) in EmotionCodes.OrderBy(k => k.Key))
            sb.AppendLine($"  {name,-18} → {code}");
        sb.AppendLine();
        sb.AppendLine("## Flag Signals  (flag → trigger words)");
        foreach (var (flag, words) in FlagSignals.OrderBy(k => k.Key))
            sb.AppendLine($"  {flag,-12} → {string.Join(", ", words)}");
        sb.AppendLine();
        sb.AppendLine("## Compression Steps");
        sb.AppendLine("  1. Entity substitution  — whole-word, case-insensitive (configured per instance)");
        sb.AppendLine("  2. Topic extraction     — top 3 keywords by TF; capitalised words boosted ×3");
        sb.AppendLine("  3. Key sentence         — highest decision-word score, truncated to 55 chars");
        sb.AppendLine("  4. Emotion detection    — signal-word matching against EmotionSignals table");
        sb.AppendLine("  5. Flag detection       — signal-word matching against FlagSignals table");
        sb.AppendLine();
        sb.AppendLine("## Token Estimate");
        sb.AppendLine("  CountTokens(text) ≈ text.Length / 3");
        return sb.ToString();
    }

    public CompressionStats GetCompressionStats(string original, string compressed)
    {
        var origChars  = original.Length;
        var compChars  = compressed.Length;
        var origTokens = CountTokens(original);
        var compTokens = CountTokens(compressed);
        var ratio      = origChars > 0 ? (double)compChars / origChars : 0.0;

        return new CompressionStats
        {
            OriginalChars    = origChars,
            CompressedChars  = compChars,
            OriginalTokens   = origTokens,
            CompressedTokens = compTokens,
            CompressionRatio = ratio,
        };
    }
}
