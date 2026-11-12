using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisHud.Core;

namespace JarvisHud.Gateway;

/// <summary>
/// Turns what the Gateway knows into a GrowthSnapshot. With the jarvis-hud plugin installed it
/// is one call; without it the same picture is assembled from the core read-only RPCs. Every
/// call is independent, so a method the Gateway lacks or refuses just leaves its domain out.
/// </summary>
internal static class SnapshotBuilder
{
    public const string PluginMethod = "jarvishud.snapshot";
    public const string PluginChangedEvent = "plugin.jarvis-hud.changed";

    public static async Task<GrowthSnapshot> BuildAsync(GatewayClient client, string agentId, CancellationToken ct)
    {
        if (client.Supports(PluginMethod))
        {
            try
            {
                var payload = await client.CallAsync(PluginMethod, new JsonObject { ["agentId"] = agentId }, ct: ct);
                var snapshot = GrowthSnapshot.FromJson(payload, agentId, GrowthSnapshot.SourcePlugin);
                // The plugin cannot know which device is asking, so "this PC" is decided here.
                var localName = LocalProbe.ReadNodeLauncher()?.DisplayName ?? Environment.MachineName;
                return snapshot with
                {
                    GatewayVersion = client.ServerVersion,
                    Devices = snapshot.Devices
                        .Select(d => d with { IsSelf = string.Equals(d.Name, localName, StringComparison.OrdinalIgnoreCase) })
                        .ToList(),
                };
            }
            catch (GatewayException)
            {
                // The plugin is present but failed: fall back to the core methods below.
            }
        }

        return await BuildFromCoreAsync(client, agentId, ct);
    }

    private static async Task<GrowthSnapshot> BuildFromCoreAsync(GatewayClient client, string agentId, CancellationToken ct)
    {
        Task<JsonElement?> Try(string method, JsonObject? parameters = null) => TryCall(client, method, parameters, ct);

        // All operator.read. Params objects are closed on the Gateway side, so only documented keys are sent.
        var nodesTask = Try("node.list");
        var agentsTask = Try("agents.list");
        var skillsTask = Try("skills.status", new JsonObject { ["agentId"] = agentId });
        var toolsTask = Try("tools.catalog", new JsonObject { ["agentId"] = agentId });
        var memoryTask = Try("memory.status", new JsonObject { ["agentId"] = agentId });
        var sessionsTask = Try("sessions.list", new JsonObject { ["agentId"] = agentId, ["limit"] = 1 });
        var pluginsTask = Try("plugins.list");
        var cronTask = Try("cron.list", new JsonObject { ["limit"] = 200 });
        var channelsTask = Try("channels.status");
        var systemTask = Try("system.info");
        // wiki.status only exists when the memory-wiki plugin is loaded.
        var wikiTask = client.Supports("wiki.status")
            ? Try("wiki.status", new JsonObject { ["agentId"] = agentId })
            : Task.FromResult<JsonElement?>(null);

        await Task.WhenAll(nodesTask, agentsTask, skillsTask, toolsTask, memoryTask, sessionsTask, pluginsTask,
            cronTask, channelsTask, systemTask, wikiTask);

        var agent = ReadAgent(agentsTask.Result, agentId);
        var devices = ReadDevices(nodesTask.Result, systemTask.Result);
        var domains = new List<DomainInfo>();

        // ---- capabilities ----
        if (skillsTask.Result is { } skills && skills.TryGetProperty("skills", out var skillList) && skillList.ValueKind == JsonValueKind.Array)
        {
            var names = skillList.EnumerateArray()
                .Where(s => Flag(s, "eligible") && !Flag(s, "disabled") && !Flag(s, "blockedByAgentFilter"))
                .Select(s => Str(s, "name"))
                .OfType<string>()
                .ToList();
            Add(domains, "skills", "Skills", DomainKind.Capability, names);
        }

        if (toolsTask.Result is { } tools && tools.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            var ids = groups.EnumerateArray()
                .Where(g => g.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array)
                .SelectMany(g => g.GetProperty("tools").EnumerateArray())
                .Select(t => Str(t, "id"))
                .OfType<string>()
                .Distinct()
                .ToList();
            Add(domains, "tools", "Tools", DomainKind.Capability, ids);
        }

        if (pluginsTask.Result is { } plugins && plugins.TryGetProperty("plugins", out var pluginList) && pluginList.ValueKind == JsonValueKind.Array)
        {
            var ids = pluginList.EnumerateArray()
                .Where(p => Flag(p, "enabled"))
                .Select(p => Str(p, "id"))
                .OfType<string>()
                .ToList();
            Add(domains, "plugins", "Plugins", DomainKind.Capability, ids);
        }

        if (cronTask.Result is { } cron && cron.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
        {
            var names = jobs.EnumerateArray()
                .Where(j => Flag(j, "enabled") && (Str(j, "agentId") is not { } owner || owner == agentId))
                .Select(j => Str(j, "name") ?? Str(j, "id"))
                .OfType<string>()
                .ToList();
            Add(domains, "automation", "Automation", DomainKind.Capability, names);
        }

        // ---- knowledge ----
        if (memoryTask.Result is { } memory &&
            memory.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("legacy", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            // The built-in memory provider reports indexed files and chunks; chunks are the finer measure.
            var count = Int(legacy, "chunks") ?? Int(legacy, "files") ?? 0;
            if (count > 0) domains.Add(new DomainInfo("memory", "Memory", DomainKind.Knowledge, count, Array.Empty<string>()));
        }

        if (wikiTask.Result is { } wiki && wiki.TryGetProperty("pageCounts", out var pages) && pages.ValueKind == JsonValueKind.Object)
        {
            var count = pages.EnumerateObject().Sum(p => p.Value.TryGetInt32(out var n) ? n : 0);
            if (count > 0) domains.Add(new DomainInfo("wiki", "Wiki", DomainKind.Knowledge, count, Array.Empty<string>()));
        }

        if (sessionsTask.Result is { } sessions && (Int(sessions, "totalCount") ?? Int(sessions, "count")) is { } sessionCount and > 0)
            domains.Add(new DomainInfo("sessions", "Sessions", DomainKind.Knowledge, sessionCount, Array.Empty<string>()));

        // ---- access ----
        var commands = devices.Where(d => d.Online).SelectMany(d => d.Commands).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        Add(domains, "commands", "Node commands", DomainKind.Access, commands);

        if (channelsTask.Result is { } channels && channels.TryGetProperty("channelOrder", out var order) && order.ValueKind == JsonValueKind.Array)
        {
            var ids = order.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToList();
            Add(domains, "channels", "Channels", DomainKind.Access, ids);
        }

        return new GrowthSnapshot(
            GrowthSnapshot.SourceGatewayRpc,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            agent,
            devices,
            domains,
            client.ServerVersion);
    }

    // A configured agent is not necessarily running. Never infer its health from a node.
    public static async Task<IReadOnlyList<AgentInventoryItem>> ReadInventoryAsync(GatewayClient client, CancellationToken ct)
    {
        var rows = new List<AgentInventoryItem>();
        var result = await TryCall(client, "agents.list", null, ct);
        if (result is { } root && root.TryGetProperty("agents", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var id = Str(item, "id");
                if (id is null) continue;
                var identity = item.TryGetProperty("identity", out var i) ? i : default;
                rows.Add(new AgentInventoryItem(Str(identity, "name") ?? Str(item, "name") ?? id,
                    $"OpenClaw · {id}", "Registered · Gateway"));
            }
        }
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "agent-inventory.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
                foreach (var item in doc.RootElement.EnumerateArray())
                    rows.Add(new AgentInventoryItem(Str(item, "name") ?? "Agent", Str(item, "detail") ?? "", "Inventory · Health not monitored"));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return rows;
    }

    private static AgentInfo ReadAgent(JsonElement? agents, string agentId)
    {
        if (agents is { } root && root.TryGetProperty("agents", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                if (Str(a, "id") != agentId) continue;
                var identity = a.TryGetProperty("identity", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                var model = a.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "primary") : null;
                return new AgentInfo(
                    agentId,
                    Str(identity, "name") ?? Str(a, "name") ?? GrowthSnapshot.Capitalize(agentId),
                    model,
                    Str(identity, "emoji"));
            }
        }
        return new AgentInfo(agentId, GrowthSnapshot.Capitalize(agentId));
    }

    private static List<DeviceInfo> ReadDevices(JsonElement? nodes, JsonElement? system)
    {
        var devices = new List<DeviceInfo>();
        var localName = LocalProbe.ReadNodeLauncher()?.DisplayName ?? Environment.MachineName;
        var gatewayIsNode = false;

        if (nodes is { } root && root.TryGetProperty("nodes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in list.EnumerateArray())
            {
                var id = Str(n, "nodeId");
                if (id is null) continue;
                var name = Str(n, "displayName") ?? id;
                var gatewayLocal = Flag(n, "gatewayLocal");
                gatewayIsNode |= gatewayLocal;
                devices.Add(new DeviceInfo(
                    id,
                    name,
                    NormalizePlatform(Str(n, "platform")),
                    gatewayLocal ? "gateway" : "node",
                    Flag(n, "connected"),
                    string.Equals(name, localName, StringComparison.OrdinalIgnoreCase),
                    StrList(n, "caps"),
                    StrList(n, "commands")));
            }
        }

        // The Gateway host is part of the homelab even when it runs no node of its own.
        if (!gatewayIsNode && system is { } info && (Str(info, "machineName") ?? Str(info, "hostname")) is { } host)
        {
            devices.Add(new DeviceInfo(
                "gateway:" + host,
                host,
                NormalizePlatform(Str(info, "platform")),
                "gateway",
                Online: true,
                IsSelf: false,
                Array.Empty<string>(),
                Array.Empty<string>()));
        }

        return devices;
    }

    /// <summary>Node platforms arrive as "macos 15.1", "darwin", "win32"…; reduce to a family name.</summary>
    private static string NormalizePlatform(string? platform)
    {
        var p = (platform ?? "").Trim().ToLowerInvariant();
        if (p.Length == 0) return "unknown";
        if (p.StartsWith("darwin") || p.StartsWith("mac")) return "macos";
        if (p.StartsWith("win")) return "windows";
        if (p.StartsWith("ios") || p.StartsWith("iphone") || p.StartsWith("ipad")) return "ios";
        var space = p.IndexOf(' ');
        return space > 0 ? p[..space] : p;
    }

    private static void Add(List<DomainInfo> domains, string id, string label, DomainKind kind, IReadOnlyList<string> items)
    {
        if (items.Count > 0) domains.Add(new DomainInfo(id, label, kind, items.Count, items));
    }

    private static async Task<JsonElement?> TryCall(GatewayClient client, string method, JsonObject? parameters, CancellationToken ct)
    {
        try
        {
            return await client.CallAsync(method, parameters, TimeSpan.FromSeconds(20), ct);
        }
        catch (Exception e) when (e is GatewayException or TimeoutException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

    private static IReadOnlyList<string> StrList(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(i => i.GetString()).OfType<string>().ToList()
            : Array.Empty<string>();
}
