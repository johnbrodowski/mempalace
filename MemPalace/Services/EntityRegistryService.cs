using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MemPalace.Core;

public sealed class EntityRegistry
{
    // ── Common English words that can collide with person names ───────────────

    private static readonly HashSet<string> CommonEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ever", "grace", "will", "bill", "mark", "april", "may", "june",
        "dawn", "faith", "hope", "joy", "pat", "iris", "violet", "crystal",
        "amber", "ruby", "jade", "rose", "lily", "ivy", "sage", "ash",
        "cliff", "glen", "gene", "rich", "frank", "earnest", "hunter",
        "chase", "reed", "clay", "wade", "brock", "lance", "ace", "leo",
        "rex", "max", "jack", "john", "james", "mike", "adam", "alan",
    };

    // ── Context indicators that suggest a word is being used as a person name ─

    private static readonly string[] PersonContextIndicators =
        ["said", "asked", "told", "replied", "went", "came", "got", "had",
         "was", "is", "has", "did", "made", "felt", "hi ", "hey ", "dear "];

    // ── State ─────────────────────────────────────────────────────────────────

    public string Mode { get; private set; } = "personal";

    private Dictionary<string, EntityInfo> _people   = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>                _projects  = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>                _ambiguousFlags = new(StringComparer.OrdinalIgnoreCase);

    // ── Serialization helpers ─────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Load the registry from <paramref name="registryPath"/> (or the default
    /// ~/.mempalace/entity_registry.json).  Returns an empty registry when the
    /// file does not exist.
    /// </summary>
    public static EntityRegistry Load(string? registryPath = null)
    {
        string path = registryPath ?? DefaultPath();

        if (!File.Exists(path))
            return new EntityRegistry();

        try
        {
            string json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RegistryDto>(json, JsonOpts);
            if (dto is null)
                return new EntityRegistry();

            var registry = new EntityRegistry
            {
                Mode           = dto.Mode ?? "personal",
                _people        = dto.People        ?? new(StringComparer.OrdinalIgnoreCase),
                _projects      = dto.Projects is not null
                                     ? new HashSet<string>(dto.Projects, StringComparer.OrdinalIgnoreCase)
                                     : new(StringComparer.OrdinalIgnoreCase),
                _ambiguousFlags = dto.AmbiguousFlags is not null
                                     ? new HashSet<string>(dto.AmbiguousFlags, StringComparer.OrdinalIgnoreCase)
                                     : new(StringComparer.OrdinalIgnoreCase),
            };
            return registry;
        }
        catch
        {
            // Corrupted file — start fresh
            return new EntityRegistry();
        }
    }

    /// <summary>
    /// Persist the registry to <paramref name="registryPath"/> (or the default path).
    /// </summary>
    public void Save(string? registryPath = null)
    {
        string path = registryPath ?? DefaultPath();

        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var dto = new RegistryDto
        {
            Mode           = Mode,
            People         = _people,
            Projects       = _projects.ToList(),
            AmbiguousFlags = _ambiguousFlags.ToList(),
        };

        string json = JsonSerializer.Serialize(dto, JsonOpts);
        File.WriteAllText(path, json);
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Populate the registry from onboarding data.
    /// </summary>
    public void Seed(
        string mode,
        IEnumerable<PersonEntry> people,
        IEnumerable<string>? projects = null,
        Dictionary<string, string>? aliases = null)
    {
        Mode = mode;

        foreach (var entry in people)
        {
            var info = new EntityInfo
            {
                Source       = "onboarding",
                Confidence   = 1.0,
                Relationship = entry.Relationship,
                Aliases      = aliases is not null && aliases.TryGetValue(entry.Name, out string? alias)
                                   ? [alias]
                                   : [],
            };
            _people[entry.Name] = info;

            // Mark collisions with common English words
            if (CommonEnglishWords.Contains(entry.Name))
                _ambiguousFlags.Add(entry.Name);
        }

        if (projects is not null)
        {
            foreach (string proj in projects)
                _projects.Add(proj);
        }
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classify a single <paramref name="word"/> using the registry, optionally
    /// guided by surrounding <paramref name="context"/>.
    /// </summary>
    public EntityLookupResult Lookup(string word, string context = "")
    {
        // Known person?
        if (_people.TryGetValue(word, out EntityInfo? info))
        {
            return new EntityLookupResult
            {
                Word           = word,
                Classification = "person",
                Confidence     = info.Confidence,
                Relationship   = info.Relationship,
            };
        }

        // Known project?
        if (_projects.Contains(word))
        {
            return new EntityLookupResult
            {
                Word           = word,
                Classification = "project",
                Confidence     = 0.9,
            };
        }

        // Common English word — use context to disambiguate
        if (CommonEnglishWords.Contains(word))
        {
            bool looksLikePerson = PersonContextIndicators.Any(indicator =>
                context.Contains(indicator, StringComparison.OrdinalIgnoreCase));

            return new EntityLookupResult
            {
                Word           = word,
                Classification = looksLikePerson ? "person" : "common",
                Confidence     = looksLikePerson ? 0.6 : 0.8,
            };
        }

        return new EntityLookupResult
        {
            Word           = word,
            Classification = "unknown",
            Confidence     = 0.0,
        };
    }

    // ── Learning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Run entity detection over <paramref name="text"/> and absorb any
    /// high-confidence new people into the registry.
    /// </summary>
    public IReadOnlyList<EntityLookupResult> LearnFromText(string text, double minConfidence = 0.75)
    {
        var added = new List<EntityLookupResult>();

        var candidates = EntityDetectorService.ExtractCandidates(text);
        if (candidates.Count == 0)
            return added;

        string[] lines = text.Split('\n');

        foreach (var (name, count) in candidates)
        {
            if (count < 3) continue;

            // Skip names already in the registry
            if (_people.ContainsKey(name)) continue;

            string classification = EntityDetectorService.ClassifyEntity(name, count, text, lines);
            if (classification != "person") continue;

            // Assign a simple confidence based on frequency
            double confidence = Math.Min(0.5 + (count * 0.05), 1.0);
            if (confidence < minConfidence) continue;

            var info = new EntityInfo
            {
                Source     = "learned",
                Confidence = confidence,
            };
            _people[name] = info;

            var result = new EntityLookupResult
            {
                Word           = name,
                Classification = "person",
                Confidence     = confidence,
            };
            added.Add(result);
        }

        return added.AsReadOnly();
    }

    // ── Query helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Extract capitalized tokens from <paramref name="query"/> and return
    /// those that match a known person (case-insensitive).
    /// </summary>
    public IReadOnlyList<string> ExtractPeopleFromQuery(string query)
    {
        var capitalized = Regex.Matches(query, @"\b([A-Z][a-zA-Z]*)\b")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return capitalized
            .Where(word => _people.ContainsKey(word))
            .ToList()
            .AsReadOnly();
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Human-readable one-liner describing the current registry state.
    /// </summary>
    public string Summary() =>
        $"Registry: {_people.Count} people, {_projects.Count} projects (mode: {Mode})";

    // ── Default path ──────────────────────────────────────────────────────────

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mempalace",
        "entity_registry.json");

    // ── Private DTO for JSON round-trip ───────────────────────────────────────

    private sealed class RegistryDto
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("people")]
        public Dictionary<string, EntityInfo>? People { get; set; }

        [JsonPropertyName("projects")]
        public List<string>? Projects { get; set; }

        [JsonPropertyName("ambiguous_flags")]
        public List<string>? AmbiguousFlags { get; set; }
    }
}
