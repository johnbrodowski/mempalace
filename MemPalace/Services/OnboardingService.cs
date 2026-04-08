namespace MemPalace.Core;

/// <summary>
/// Interactive first-run wizard that gathers user preferences, detects entities,
/// and writes bootstrap files to ~/.mempalace/.
/// </summary>
public sealed class OnboardingService
{
    // ── Default domain sets per mode ──────────────────────────────────────────

    private static readonly Dictionary<string, string[]> DefaultDomains =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["work"]     = ["projects", "clients", "team", "decisions", "research"],
            ["personal"] = ["family", "health", "creative", "reflections", "relationships"],
            ["combo"]    = ["family", "work", "health", "creative", "projects", "reflections"],
        };

    private readonly AppConfig       _config;
    private readonly DatabaseService _db;

    public OnboardingService(AppConfig config, DatabaseService db)
    {
        _config = config;
        _db     = db;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Run the interactive onboarding wizard.
    /// </summary>
    public async Task<EntityRegistry> RunOnboardingAsync(string directory = ".", bool autoDetect = true)
    {
        PrintWelcomeBanner();

        // Step 1: mode
        string mode = AskMode();

        // Step 2: auto-detect entities from directory
        var detected = new DetectedEntities();
        if (autoDetect)
        {
            var files = EntityDetectorService.ScanForDetection(directory);
            detected  = EntityDetectorService.DetectEntities(files);
        }

        // Step 3: people
        var (people, aliases) = AskPeople(mode, detected);

        // Step 4: projects
        var projects = AskProjects(mode, detected);

        // Step 5: domains
        var domains = AskDomains(mode);

        // Step 6: build and seed registry
        var registry = new EntityRegistry();
        registry.Seed(mode, people, projects, aliases);

        // Step 7: generate bootstrap files
        GenerateAaakBootstrap(people, projects, domains, mode);

        // Step 8: persist registry
        registry.Save(_config.EntityRegistryPath);

        await Task.CompletedTask;
        return registry;
    }

    /// <summary>
    /// Non-interactive setup — build and persist the registry from supplied data.
    /// </summary>
    public EntityRegistry QuickSetup(
        string mode,
        IEnumerable<PersonEntry> people,
        IEnumerable<string>?     projects = null,
        Dictionary<string, string>? aliases = null)
    {
        var registry = new EntityRegistry();
        registry.Seed(mode, people, projects, aliases);
        registry.Save(_config.EntityRegistryPath);
        return registry;
    }

    // -------------------------------------------------------------------------
    // Interactive prompts
    // -------------------------------------------------------------------------

    private string AskMode()
    {
        Console.Write("Select mode: work / personal / combo [personal]: ");
        string? input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(input))
            return "personal";

        if (DefaultDomains.ContainsKey(input))
            return input;

        Console.WriteLine($"Unknown mode '{input}', defaulting to 'personal'.");
        return "personal";
    }

    private (List<PersonEntry> people, Dictionary<string, string> aliases) AskPeople(
        string mode, DetectedEntities detected)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (detected.People.Count > 0)
        {
            string detectedList = string.Join(", ", detected.People);
            Console.Write($"Detected people: {detectedList}. Press Enter to confirm or type corrections: ");
        }
        else
        {
            Console.Write("Enter people you interact with (comma-separated, optionally name:relationship): ");
        }

        string? input = Console.ReadLine()?.Trim();

        // If the user pressed Enter with detected people, use the detected list
        if (string.IsNullOrEmpty(input) && detected.People.Count > 0)
        {
            var people = detected.People
                .Select(name => new PersonEntry { Name = name })
                .ToList();
            return (people, aliases);
        }

        return (ParsePeopleInput(input ?? ""), aliases);
    }

    private List<string> AskProjects(string mode, DetectedEntities detected)
    {
        if (detected.Projects.Count > 0)
        {
            string detectedList = string.Join(", ", detected.Projects);
            Console.Write($"Detected projects: {detectedList}. Press Enter to confirm or type corrections: ");
        }
        else
        {
            Console.Write("Enter projects you work on (comma-separated): ");
        }

        string? input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input) && detected.Projects.Count > 0)
            return detected.Projects.ToList();

        if (string.IsNullOrEmpty(input))
            return [];

        return input.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .ToList();
    }

    private List<string> AskDomains(string mode)
    {
        string[] defaults = DefaultDomains.TryGetValue(mode, out var d) ? d : DefaultDomains["personal"];
        string defaultList = string.Join(", ", defaults);

        Console.WriteLine($"Default domains for '{mode}' mode: {defaultList}");
        Console.Write("Add or remove domains? (Enter to keep defaults, or +add,-remove): ");

        string? input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input))
            return defaults.ToList();

        var domains = defaults.ToList();

        foreach (string token in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.Trim();
            if (t.StartsWith('+'))
            {
                string domain = t[1..].Trim();
                if (domain.Length > 0 && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                    domains.Add(domain);
            }
            else if (t.StartsWith('-'))
            {
                string domain = t[1..].Trim();
                domains.RemoveAll(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
            }
        }

        return domains;
    }

    // -------------------------------------------------------------------------
    // Bootstrap file generation
    // -------------------------------------------------------------------------

    private void GenerateAaakBootstrap(
        IEnumerable<PersonEntry> people,
        IEnumerable<string>      projects,
        IEnumerable<string>      domains,
        string                   mode)
    {
        string mempalaceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mempalace");
        Directory.CreateDirectory(mempalaceDir);

        // ── Entity codes ──────────────────────────────────────────────────────
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var peopleList   = people.ToList();
        var projectsList = projects.ToList();
        var domainsList  = domains.ToList();

        // Assign codes to people
        foreach (var person in peopleList)
        {
            if (!string.IsNullOrEmpty(person.Code))
            {
                usedCodes.Add(person.Code);
                continue;
            }
            // PersonEntry is a record — we need to work around immutability via helper
            // (Code is settable via init; we assign via the helper below)
        }

        var peopleWithCodes = peopleList
            .Select(p => (Entry: p, Code: AssignCode(p.Name, usedCodes)))
            .ToList();

        var projectsWithCodes = projectsList
            .Select(proj => (Name: proj, Code: AssignCode(proj, usedCodes)))
            .ToList();

        // ── aaak_entities.md ─────────────────────────────────────────────────
        var entitiesSb = new System.Text.StringBuilder();
        entitiesSb.AppendLine("# AAAK Entity Codes");
        entitiesSb.AppendLine("Generated by MemPalace onboarding.");
        entitiesSb.AppendLine();
        entitiesSb.AppendLine("## People");
        entitiesSb.AppendLine("| Name | Code | Relationship |");
        entitiesSb.AppendLine("|------|------|-------------|");
        foreach (var (entry, code) in peopleWithCodes)
            entitiesSb.AppendLine($"| {entry.Name} | {code} | {entry.Relationship} |");

        entitiesSb.AppendLine();
        entitiesSb.AppendLine("## Projects");
        entitiesSb.AppendLine("| Name | Code |");
        entitiesSb.AppendLine("|------|------|");
        foreach (var (name, code) in projectsWithCodes)
            entitiesSb.AppendLine($"| {name} | {code} |");

        File.WriteAllText(
            Path.Combine(mempalaceDir, "aaak_entities.md"),
            entitiesSb.ToString());

        // ── critical_facts.md ─────────────────────────────────────────────────
        var factsSb = new System.Text.StringBuilder();
        factsSb.AppendLine("# Critical Facts");
        factsSb.AppendLine("Generated by MemPalace onboarding.");
        factsSb.AppendLine($"Mode: {mode}");
        factsSb.AppendLine($"Domains: {string.Join(", ", domainsList)}");
        factsSb.AppendLine($"People: {string.Join(", ", peopleList.Select(p => p.Name))}");
        factsSb.AppendLine($"Projects: {string.Join(", ", projectsList)}");

        File.WriteAllText(
            Path.Combine(mempalaceDir, "critical_facts.md"),
            factsSb.ToString());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void PrintWelcomeBanner()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║       Welcome to MemPalace           ║");
        Console.WriteLine("║    Your personal memory store        ║");
        Console.WriteLine("╚══════════════════════════════════════╝");
        Console.WriteLine();
    }

    /// <summary>
    /// Parse a comma-separated string of people entries.
    /// Each entry can be "Name" or "Name:relationship".
    /// </summary>
    private static List<PersonEntry> ParsePeopleInput(string input)
    {
        var people = new List<PersonEntry>();

        foreach (string token in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.Trim();
            if (t.Length == 0) continue;

            int colonIdx = t.IndexOf(':');
            if (colonIdx > 0)
            {
                string name         = t[..colonIdx].Trim();
                string relationship = t[(colonIdx + 1)..].Trim();
                if (name.Length > 0)
                    people.Add(new PersonEntry { Name = name, Relationship = relationship });
            }
            else
            {
                people.Add(new PersonEntry { Name = t });
            }
        }

        return people;
    }

    /// <summary>
    /// Generate a 3-character uppercase code for <paramref name="name"/> that
    /// does not collide with any code already in <paramref name="usedCodes"/>.
    /// Falls back to progressively offset substrings if collisions occur.
    /// </summary>
    private static string AssignCode(string name, HashSet<string> usedCodes)
    {
        string upper = name.ToUpperInvariant().Replace(" ", "");
        if (upper.Length == 0) upper = "XXX";

        // Pad if too short
        while (upper.Length < 3)
            upper += upper[^1];

        // Try first 3 chars, then offset windows
        string candidate = upper[..3];
        if (!usedCodes.Contains(candidate))
        {
            usedCodes.Add(candidate);
            return candidate;
        }

        // Slide window across available characters
        for (int start = 1; start <= upper.Length - 3; start++)
        {
            candidate = upper[start..(start + 3)];
            if (!usedCodes.Contains(candidate))
            {
                usedCodes.Add(candidate);
                return candidate;
            }
        }

        // Append numeric suffix until unique
        int suffix = 1;
        while (true)
        {
            candidate = upper[..2] + suffix.ToString();
            if (!usedCodes.Contains(candidate))
            {
                usedCodes.Add(candidate);
                return candidate;
            }
            suffix++;
        }
    }
}
