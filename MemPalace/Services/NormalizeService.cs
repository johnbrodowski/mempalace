using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MemPalace.Core;

public static class NormalizeService
{
    // ── Backward-compat helpers ──────────────────────────────────────────────

    /// <summary>Collapse runs of whitespace into a single space and trim.</summary>
    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return string.Join(' ', text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Lowercase and replace spaces with hyphens.</summary>
    public static string NormalizeRoomName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return name.ToLowerInvariant().Replace(' ', '-');
    }

    // ── Main entry point ─────────────────────────────────────────────────────

    /// <summary>
    /// Read <paramref name="filePath"/> and return a normalised transcript.
    /// If the file already looks like a transcript (3+ lines starting with '&gt;')
    /// it is returned as-is.  Otherwise the content is parsed as one of the
    /// known JSON chat-export formats.
    /// </summary>
    public static string Normalize(string filePath)
    {
        string content = File.ReadAllText(filePath, Encoding.UTF8);

        // Already a transcript?
        int quoteLines = content
            .Split('\n')
            .Count(l => l.TrimStart().StartsWith('>'));
        if (quoteLines >= 3)
            return content;

        // JSONL path (Claude Code or any one-JSON-per-line file)
        bool looksLikeJsonl = filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || IsJsonLines(content);

        if (looksLikeJsonl)
        {
            string? result = TryClaudeCodeJsonl(content);
            if (result != null) return result;
        }

        // Full-JSON path
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            JsonElement root = doc.RootElement;

            string? parsed =
                TryClaudeAiJson(root) ??
                TryChatGptJson(root) ??
                TrySlackJson(root);

            if (parsed != null) return parsed;
        }
        catch (JsonException)
        {
            // Not valid JSON – fall through
        }

        // Unknown format: return plain text as-is
        return content;
    }

    // ── Internal parsers ─────────────────────────────────────────────────────

    private static string? TryClaudeCodeJsonl(string content)
    {
        var messages = new List<(string role, string text)>();

        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("type", out JsonElement typeProp)) continue;
                string type = typeProp.GetString() ?? string.Empty;

                if (type is not ("human" or "user" or "assistant")) continue;

                string role = type is "human" or "user" ? "user" : "assistant";

                // Content lives in different places depending on the JSONL producer.
                string text = string.Empty;
                if (root.TryGetProperty("content", out JsonElement contentProp))
                    text = ExtractContent(contentProp);
                else if (root.TryGetProperty("message", out JsonElement msgProp))
                    text = ExtractContent(msgProp);
                else if (root.TryGetProperty("text", out JsonElement textProp))
                    text = textProp.GetString() ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(text))
                    messages.Add((role, text));
            }
            catch (JsonException)
            {
                // Skip malformed lines
            }
        }

        return messages.Count == 0 ? null : MessagesToTranscript(messages);
    }

    private static string? TryClaudeAiJson(JsonElement data)
    {
        // Shape 0: bare array of {role, content} objects — e.g. Python json.dump([{role,content},...])
        if (data.ValueKind == JsonValueKind.Array)
        {
            // Detect by checking whether the first object element has a "role" key
            foreach (var first in data.EnumerateArray())
            {
                if (first.ValueKind != JsonValueKind.Object) break;
                if (!first.TryGetProperty("role", out _)) break;

                var list = new List<(string role, string text)>();
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string role = item.TryGetProperty("role", out var rp) ? rp.GetString() ?? "" : "";
                    string text = item.TryGetProperty("content", out var cp) ? ExtractContent(cp) : "";
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add((role, text));
                }
                return list.Count == 0 ? null : MessagesToTranscript(list);
            }
            return null;
        }

        if (data.ValueKind != JsonValueKind.Object) return null;

        // Shape 1: { "messages": [ { "role": "...", "content": ... } ] }
        if (data.TryGetProperty("messages", out JsonElement messagesEl)
            && messagesEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<(string role, string text)>();
            foreach (JsonElement item in messagesEl.EnumerateArray())
            {
                string role = string.Empty;
                if (item.TryGetProperty("role", out JsonElement roleProp))
                    role = roleProp.GetString() ?? string.Empty;

                if (string.IsNullOrEmpty(role)) continue;

                string text = string.Empty;
                if (item.TryGetProperty("content", out JsonElement contentProp))
                    text = ExtractContent(contentProp);

                if (!string.IsNullOrWhiteSpace(text))
                    list.Add((role, text));
            }
            if (list.Count > 0) return MessagesToTranscript(list);
        }

        // Shape 2: { "chat_messages": [ { "sender": "...", "text": "..." } ] }
        if (data.TryGetProperty("chat_messages", out JsonElement chatMsgsEl)
            && chatMsgsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<(string role, string text)>();
            foreach (JsonElement item in chatMsgsEl.EnumerateArray())
            {
                string sender = string.Empty;
                if (item.TryGetProperty("sender", out JsonElement senderProp))
                    sender = senderProp.GetString() ?? string.Empty;

                string text = string.Empty;
                if (item.TryGetProperty("text", out JsonElement textProp))
                    text = textProp.GetString() ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(text))
                    list.Add((sender, text));
            }
            if (list.Count > 0) return MessagesToTranscript(list);
        }

        return null;
    }

    private static string? TryChatGptJson(JsonElement data)
    {
        // ChatGPT conversations.json: top-level array or single conversation object
        if (data.ValueKind == JsonValueKind.Array)
        {
            var allMessages = new List<(string role, string text)>();
            foreach (JsonElement conversation in data.EnumerateArray())
            {
                var msgs = ExtractChatGptConversation(conversation);
                allMessages.AddRange(msgs);
            }
            return allMessages.Count == 0 ? null : MessagesToTranscript(allMessages);
        }

        if (!data.TryGetProperty("mapping", out _)) return null;

        var messages = ExtractChatGptConversation(data);
        return messages.Count == 0 ? null : MessagesToTranscript(messages);
    }

    private static List<(string role, string text)> ExtractChatGptConversation(JsonElement conversation)
    {
        if (!conversation.TryGetProperty("mapping", out JsonElement mapping)
            || mapping.ValueKind != JsonValueKind.Object)
            return [];

        // Build node dict
        var nodes = new Dictionary<string, JsonElement>();
        foreach (JsonProperty prop in mapping.EnumerateObject())
            nodes[prop.Name] = prop.Value;

        // Find the root: node whose parent is null/missing
        string? rootId = null;
        foreach (var (id, node) in nodes)
        {
            bool hasParent = node.TryGetProperty("parent", out JsonElement parentProp)
                             && parentProp.ValueKind != JsonValueKind.Null
                             && !string.IsNullOrEmpty(parentProp.GetString());
            if (!hasParent) { rootId = id; break; }
        }

        if (rootId == null) return [];

        // Follow first child linearly (non-recursive)
        var messages = new List<(string role, string text)>();
        string? currentId = rootId;

        while (currentId != null)
        {
            if (!nodes.TryGetValue(currentId, out JsonElement node)) break;

            // Extract message if present
            if (node.TryGetProperty("message", out JsonElement msg)
                && msg.ValueKind != JsonValueKind.Null)
            {
                string role = string.Empty;
                if (msg.TryGetProperty("author", out JsonElement author)
                    && author.TryGetProperty("role", out JsonElement roleProp))
                    role = roleProp.GetString() ?? string.Empty;

                if (role != "system" && !string.IsNullOrEmpty(role))
                {
                    string text = string.Empty;
                    if (msg.TryGetProperty("content", out JsonElement contentEl))
                    {
                        if (contentEl.TryGetProperty("parts", out JsonElement parts)
                            && parts.ValueKind == JsonValueKind.Array)
                        {
                            var sb = new StringBuilder();
                            foreach (JsonElement part in parts.EnumerateArray())
                                sb.Append(ExtractContent(part));
                            text = sb.ToString().Trim();
                        }
                        else
                        {
                            text = ExtractContent(contentEl);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                        messages.Add((role, text));
                }
            }

            // Advance to first child only
            currentId = null;
            if (node.TryGetProperty("children", out JsonElement children)
                && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    currentId = child.GetString();
                    break; // first child only
                }
            }
        }

        return messages;
    }

    private static string? TrySlackJson(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return null;

        var messages = new List<(string role, string text)>();
        string? firstUser = null;

        foreach (JsonElement item in data.EnumerateArray())
        {
            string user = string.Empty;
            if (item.TryGetProperty("user", out JsonElement userProp))
                user = userProp.GetString() ?? string.Empty;

            string text = string.Empty;
            if (item.TryGetProperty("text", out JsonElement textProp))
                text = textProp.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(text))
                continue;

            // Map the first seen user to "user", everyone else to "assistant"
            firstUser ??= user;
            string role = user == firstUser ? "user" : "assistant";

            messages.Add((role, text));
        }

        return messages.Count == 0 ? null : MessagesToTranscript(messages);
    }

    private static string ExtractContent(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array  => ExtractFromArray(content),
            JsonValueKind.Object => ExtractFromObject(content),
            _                    => string.Empty
        };
    }

    private static string ExtractFromArray(JsonElement array)
    {
        var parts = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
            parts.Add(ExtractContent(item));
        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string ExtractFromObject(JsonElement obj)
    {
        if (obj.TryGetProperty("text", out JsonElement textProp))
            return textProp.GetString() ?? string.Empty;
        if (obj.TryGetProperty("content", out JsonElement contentProp))
            return ExtractContent(contentProp);
        return string.Empty;
    }

    private static string MessagesToTranscript(List<(string role, string text)> messages)
    {
        var sb = new StringBuilder();
        bool first = true;

        foreach (var (role, text) in messages)
        {
            if (!first) sb.AppendLine();
            first = false;

            bool isUser = role is "user" or "human";
            if (isUser)
                sb.AppendLine($"> {text.Trim()}");
            else
                sb.AppendLine(text.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when every non-empty line in the content is valid JSON
    /// (heuristic: at least 2 lines and all of the first 5 lines parse).
    /// </summary>
    private static bool IsJsonLines(string content)
    {
        string[] lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (lines.Length < 2) return false;

        foreach (string line in lines.Take(5))
        {
            try { JsonDocument.Parse(line).Dispose(); }
            catch (JsonException) { return false; }
        }
        return true;
    }
}
