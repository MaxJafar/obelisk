using System.Text.Json;

namespace JarvisHud.Core;

/// <summary>What the agent is doing right now. Drives the orb's motion and the status line.</summary>
public enum HudState
{
    Offline,
    Idle,
    Listening,
    Transcribing,
    Thinking,
    Executing,
    Speaking,
    Error,
}

public enum DomainKind
{
    /// <summary>Things the agent can do: skills, tools, plugins, automation.</summary>
    Capability,
    /// <summary>Things the agent knows or remembers: memory, sessions, wiki.</summary>
    Knowledge,
    /// <summary>Homelab surfaces the agent can reach: node commands, channels.</summary>
    Access,
}

public sealed record AgentInfo(string Id, string Name, string? Model = null, string? Emoji = null);

/// <summary>One machine in the homelab. Each device becomes a satellite orb.</summary>
public sealed record DeviceInfo(
    string Id,
    string Name,
    string Platform,
    string Kind,
    bool Online,
    bool IsSelf,
    IReadOnlyList<string> Caps,
    IReadOnlyList<string> Commands);

/// <summary>One growth domain. Each domain earns rings as its count grows.</summary>
public sealed record DomainInfo(
    string Id,
    string Label,
    DomainKind Kind,
    int Count,
    IReadOnlyList<string> Items);

/// <summary>
/// The single contract between the data sources (plugin, gateway RPC, local probe, preview)
/// and the renderer. Mirrors shared/growth-snapshot.schema.json.
/// </summary>
public sealed record GrowthSnapshot(
    string Source,
    long GeneratedAtMs,
    AgentInfo Agent,
    IReadOnlyList<DeviceInfo> Devices,
    IReadOnlyList<DomainInfo> Domains,
    string? GatewayVersion = null)
{
    public const int SchemaVersion = 1;
    public const int MaxRingsPerDomain = 5;

    public const string SourcePlugin = "plugin";
    public const string SourceGatewayRpc = "gateway-rpc";
    public const string SourceLocal = "local";
    public const string SourceSimulated = "simulated";

    /// <summary>
    /// Rings earned by a domain: 1 ring for 1-3 items, 2 for 4-15, 3 for 16-63, 4 for 64-255, 5 beyond.
    /// Growth is logarithmic so the hundredth memory still matters, but less than the first.
    /// </summary>
    public static int RingsFor(int count)
    {
        if (count <= 0) return 0;
        var rings = 1 + (int)Math.Floor(Math.Log2(count) / 2.0);
        return Math.Clamp(rings, 1, MaxRingsPerDomain);
    }

    /// <summary>Item count at which a domain earns its next ring, or null at the cap.</summary>
    public static int? NextRingAt(int count)
    {
        var rings = RingsFor(count);
        if (rings >= MaxRingsPerDomain) return null;
        return 1 << (2 * rings);
    }

    public int TotalRings => Domains.Sum(d => RingsFor(d.Count));
    public int OnlineDevices => Devices.Count(d => d.Online);

    public static GrowthSnapshot Empty(string agentId, string source = SourceLocal) => new(
        source,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        new AgentInfo(agentId, Capitalize(agentId)),
        Array.Empty<DeviceInfo>(),
        Array.Empty<DomainInfo>());

    /// <summary>Parses the payload the jarvis-hud plugin returns from <c>jarvishud.snapshot</c>.</summary>
    public static GrowthSnapshot FromJson(JsonElement root, string fallbackAgentId, string source)
    {
        var agentEl = root.TryGetProperty("agent", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var agentId = Str(agentEl, "id") ?? fallbackAgentId;
        var agent = new AgentInfo(
            agentId,
            Str(agentEl, "name") ?? Capitalize(agentId),
            Str(agentEl, "model"),
            Str(agentEl, "emoji"));

        var devices = new List<DeviceInfo>();
        if (root.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in devs.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                var id = Str(d, "id");
                if (string.IsNullOrEmpty(id)) continue;
                devices.Add(new DeviceInfo(
                    id,
                    Str(d, "name") ?? id,
                    (Str(d, "platform") ?? "unknown").ToLowerInvariant(),
                    Str(d, "kind") ?? "node",
                    Bool(d, "online"),
                    Bool(d, "self"),
                    StrList(d, "caps"),
                    StrList(d, "commands")));
            }
        }

        var domains = new List<DomainInfo>();
        if (root.TryGetProperty("domains", out var doms) && doms.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in doms.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                var id = Str(d, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var items = StrList(d, "items");
                var count = d.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : items.Count;
                domains.Add(new DomainInfo(
                    id,
                    Str(d, "label") ?? id,
                    ParseKind(Str(d, "kind")),
                    Math.Max(0, count),
                    items));
            }
        }

        string? gatewayVersion = null;
        if (root.TryGetProperty("gateway", out var gw) && gw.ValueKind == JsonValueKind.Object)
            gatewayVersion = Str(gw, "version");

        var generatedAt = root.TryGetProperty("generatedAt", out var g) && g.TryGetInt64(out var ms)
            ? ms
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new GrowthSnapshot(source, generatedAt, agent, devices, domains, gatewayVersion);
    }

    public static DomainKind ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "knowledge" => DomainKind.Knowledge,
        "access" => DomainKind.Access,
        _ => DomainKind.Capability,
    };

    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> StrList(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }
}
