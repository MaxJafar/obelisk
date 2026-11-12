using System.Text.Json;
using System.Text.RegularExpressions;

namespace JarvisHud.Core;

/// <summary>
/// What this machine knows about itself without talking to the Gateway: the OpenClaw node
/// installed here and the skills on disk. Used as the honest fallback view while the Gateway
/// link is down, and to prefill the Gateway address.
/// </summary>
public static partial class LocalProbe
{
    private static string OpenClawHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw");

    public sealed record NodeLauncher(string? DisplayName, string? Host, int? Port, bool Tls, IReadOnlyList<string> Commands);

    /// <summary>The Gateway address the local OpenClaw install already uses, if any.</summary>
    public static string? DiscoverGatewayUrl()
    {
        try
        {
            var config = Path.Combine(OpenClawHome, "openclaw.json");
            if (File.Exists(config))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(config),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("gateway", out var gw) &&
                    gw.TryGetProperty("remote", out var remote) &&
                    remote.TryGetProperty("url", out var url) &&
                    url.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(url.GetString()))
                    return url.GetString();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to the node launcher.
        }

        var node = ReadNodeLauncher();
        if (node?.Host is { Length: > 0 } host)
            return $"{(node.Tls ? "wss" : "ws")}://{host}:{node.Port ?? 18789}";
        return null;
    }

    /// <summary>Parses the node host launcher OpenClaw installs on Windows (~/.openclaw/node.cmd).</summary>
    public static NodeLauncher? ReadNodeLauncher()
    {
        try
        {
            var path = Path.Combine(OpenClawHome, "node.cmd");
            if (!File.Exists(path)) return null;
            var line = File.ReadLines(path).LastOrDefault(l => l.Contains(" node run", StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;

            string? Arg(string name)
            {
                var m = Regex.Match(line, $@"--{name}\s+(""[^""]+""|\S+)");
                return m.Success ? m.Groups[1].Value.Trim('"') : null;
            }

            var commands = Arg("commands")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           ?? Array.Empty<string>();
            return new NodeLauncher(
                Arg("display-name"),
                Arg("host"),
                int.TryParse(Arg("port"), out var port) ? port : null,
                TlsFlag().IsMatch(line),
                commands);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Names of the skills installed in the local OpenClaw skills folder.</summary>
    public static IReadOnlyList<string> ReadLocalSkills()
    {
        try
        {
            var dir = Path.Combine(OpenClawHome, "skills");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                .Select(d => Path.GetFileName(d)!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The offline view: only facts readable from this machine. It never guesses at what the
    /// agent knows; that needs the Gateway.
    /// </summary>
    public static GrowthSnapshot BuildSnapshot(string agentId)
    {
        var node = ReadNodeLauncher();
        var skills = ReadLocalSkills();

        var devices = new List<DeviceInfo>
        {
            new(
                Id: "local:" + Environment.MachineName,
                Name: node?.DisplayName ?? Environment.MachineName,
                Platform: "windows",
                Kind: node is null ? "client" : "node",
                Online: true,
                IsSelf: true,
                Caps: Array.Empty<string>(),
                Commands: node?.Commands ?? Array.Empty<string>()),
        };

        var domains = new List<DomainInfo>();
        if (skills.Count > 0)
            domains.Add(new DomainInfo("skills", "Skills on this node", DomainKind.Capability, skills.Count, skills));
        if (node is { Commands.Count: > 0 })
            domains.Add(new DomainInfo("commands", "Node commands", DomainKind.Access, node.Commands.Count, node.Commands));

        return new GrowthSnapshot(
            GrowthSnapshot.SourceLocal,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new AgentInfo(agentId, GrowthSnapshot.Capitalize(agentId)),
            devices,
            domains);
    }

    [GeneratedRegex(@"\s--tls(\s|$)")]
    private static partial Regex TlsFlag();
}
