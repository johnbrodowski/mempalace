using System;
using System.Collections.Generic;
using System.Linq;

namespace MemPalace.Core;

public static class RoomDetectorService
{
    // ── Keyword tables ───────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string[]> TopicKeywords =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["technical"] =
            [
                "code", "python", "function", "bug", "error", "api", "database",
                "server", "deploy", "git", "test", "debug", "refactor", "implement",
                "algorithm", "performance", "cache", "query", "endpoint", "async"
            ],
            ["architecture"] =
            [
                "architecture", "design", "pattern", "structure", "schema", "interface",
                "module", "component", "service", "layer", "dependency", "abstraction",
                "coupling", "separation", "system"
            ],
            ["planning"] =
            [
                "plan", "roadmap", "milestone", "deadline", "priority", "sprint",
                "backlog", "scope", "requirement", "spec", "timeline", "goal",
                "objective", "strategy", "estimate"
            ],
            ["decisions"] =
            [
                "decided", "chose", "picked", "switched", "migrated", "replaced",
                "trade-off", "alternative", "option", "approach", "selected",
                "went with", "choose", "choosing"
            ],
            ["problems"] =
            [
                "problem", "issue", "broken", "failed", "crash", "stuck",
                "workaround", "fix", "solved", "resolved", "error", "exception",
                "trouble", "difficult", "blocker"
            ]
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Return the topic category that best matches <paramref name="content"/>.
    /// Falls back to <c>"general"</c> when no keywords match.
    /// </summary>
    public static string DetectTopic(string content)
    {
        return ScoreBestCategory(content);
    }

    /// <summary>
    /// Map the detected topic to its corresponding category.
    /// </summary>
    public static string DetectCategory(string content)
    {
        string topic = DetectTopic(content);
        return topic switch
        {
            "decisions"    => "cat_decisions",
            "planning"     => "cat_planning",
            "architecture" => "cat_events",
            "technical"    => "cat_facts",
            "problems"     => "cat_facts",
            _              => "cat_general"
        };
    }

    /// <summary>
    /// Identical to <see cref="DetectTopic"/> – provided for naming parity with
    /// the Python <c>convo_miner.py</c> interface.
    /// </summary>
    public static string DetectConvoTopic(string content)
    {
        return ScoreBestCategory(content);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static string ScoreBestCategory(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "general";

        string lower = content.ToLowerInvariant();

        string bestCategory = "general";
        int bestScore = 0;

        foreach (var (category, keywords) in TopicKeywords)
        {
            int score = keywords.Count(kw => lower.Contains(kw, StringComparison.Ordinal));
            if (score > bestScore)
            {
                bestScore = score;
                bestCategory = category;
            }
        }

        return bestCategory;
    }
}
