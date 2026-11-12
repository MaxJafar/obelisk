using System.Text.RegularExpressions;

namespace JarvisHud.Core;

public enum OrbTopic { General, Knowledge, Systems, Creative, Planning }
public enum OrbMood { Resting, Calm, Attentive, Focused, Active, Warm, Alert }

/// <summary>
/// A small, local visual vocabulary. Topic is a keyword cue from the user's current
/// message or a known tool name; mood describes activity, never a claim about emotion.
/// No extra model call, network request, stored text, or inferred personal attributes.
/// </summary>
public static partial class OrbExpression
{
    private static readonly (OrbTopic Topic, string[] Words)[] Vocabulary =
    [
        (OrbTopic.Systems, ["code", "coding", "debug", "bug", "api", "server", "deploy", "database", "python", "typescript", "windows", "network", "automation", "код", "сервер", "программ", "ошибк", "sistem", "proqram"]),
        (OrbTopic.Knowledge, ["research", "learn", "explain", "search", "study", "memory", "wiki", "science", "history", "исслед", "объясн", "поиск", "изучи", "памят", "araşdır", "öyrən"]),
        (OrbTopic.Creative, ["design", "art", "draw", "music", "image", "video", "story", "creative", "visual", "orb", "hologram", "дизайн", "рисун", "музык", "творч", "изображ", "yarad", "dizayn"]),
        (OrbTopic.Planning, ["plan", "schedule", "calendar", "organize", "task", "meeting", "roadmap", "budget", "priority", "план", "задач", "расписан", "встреч", "təqvim", "cədvəl"]),
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    public static OrbTopic TopicForText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return OrbTopic.General;
        var words = Words().Matches(text[..Math.Min(text.Length, 4096)].ToLowerInvariant())
            .Select(m => m.Value).Distinct().ToArray();
        var best = OrbTopic.General;
        var bestScore = 0;
        foreach (var (topic, terms) in Vocabulary)
        {
            var score = terms.Count(term => words.Any(word => word == term ||
                (term.Any(c => c > 127) && word.StartsWith(term, StringComparison.Ordinal))));
            if (score <= bestScore) continue;
            best = topic;
            bestScore = score;
        }
        return best;
    }

    public static OrbTopic TopicForTool(string? tool, OrbTopic fallback)
    {
        var name = (tool ?? "").ToLowerInvariant().Replace('_', '.');
        if (name.Contains("image") || name.Contains("design") || name.Contains("music")) return OrbTopic.Creative;
        if (name.Contains("search") || name.Contains("memory") || name.Contains("fetch") || name.Contains("wiki")) return OrbTopic.Knowledge;
        if (name.Contains("calendar") || name.Contains("schedule") || name.Contains("task")) return OrbTopic.Planning;
        if (name.Contains("exec") || name.Contains("shell") || name.Contains("node") || name.Contains("file") || name.Contains("code")) return OrbTopic.Systems;
        return fallback;
    }

    public static OrbMood MoodFor(HudState state) => state switch
    {
        HudState.Idle => OrbMood.Calm,
        HudState.Listening => OrbMood.Attentive,
        HudState.Transcribing or HudState.Thinking => OrbMood.Focused,
        HudState.Executing => OrbMood.Active,
        HudState.Speaking => OrbMood.Warm,
        HudState.Error => OrbMood.Alert,
        _ => OrbMood.Resting,
    };
}
