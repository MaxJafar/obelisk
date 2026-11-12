using System.Text.Json;

namespace JarvisHud.Homelab;

public sealed class LabSnapshot
{
    public int SchemaVersion { get; set; }
    public double Timestamp { get; set; }
    public int CollectionMs { get; set; }
    public List<LabDevice> Devices { get; set; } = [];
    public List<LabService> Services { get; set; } = [];
    public List<LabAgent> Agents { get; set; } = [];
    public List<LabSession> Sessions { get; set; } = [];
    public List<LabMonitor> Monitors { get; set; } = [];
    public List<LabSource> Sources { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool IsStale => Timestamp <= 0 || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Timestamp > 120;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static LabSnapshot Parse(string json) => JsonSerializer.Deserialize<LabSnapshot>(json, Json) is { SchemaVersion: 1 } s
        ? s : throw new JsonException("Unsupported homelab snapshot");
    public string DeviceName(string id) => Devices.FirstOrDefault(d => d.Id == id)?.Name ?? id;
    public void KeepUnavailableInventory(LabSnapshot previous)
    {
        foreach (var device in Devices.Where(d => !d.Online || d.TelemetryStatus != "Connected"))
        {
            foreach (var agent in previous.Agents.Where(a => a.Device == device.Id && !Agents.Any(n => n.Id == a.Id)))
            { agent.TelemetryStale = true; Agents.Add(agent); }
            foreach (var session in previous.Sessions.Where(a => a.Device == device.Id && !Sessions.Any(n => n.Id == a.Id)))
            { session.TelemetryStale = true; Sessions.Add(session); }
        }
    }
}

public sealed class LabDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Dns { get; set; } = "";
    public string? Os { get; set; }
    public bool Online { get; set; }
    public double SampledAt { get; set; }
    public double LastSeen { get; set; }
    public double? UptimeSeconds { get; set; }
    public double? CpuPercent { get; set; }
    public double? LoadAverage { get; set; }
    public double? MemoryPercent { get; set; }
    public double? MemoryTotal { get; set; }
    public List<LabDisk> Disks { get; set; } = [];
    public string TelemetryStatus { get; set; } = "Not configured";
    public string Source { get; set; } = "";
}

public sealed class LabDisk
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mount { get; set; } = "";
    public string Filesystem { get; set; } = "";
    public double TotalBytes { get; set; }
    public double UsedBytes { get; set; }
    public double FreeBytes { get; set; }
    public double SampledAt { get; set; }
}

public sealed class LabService
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Device { get; set; } = "";
    public string Url { get; set; } = "";
    public string Status { get; set; } = "Unknown";
    public string? Detail { get; set; }
    public string? ContainerStatus { get; set; }
    public string? Container { get; set; }
    public string? ContainerId { get; set; }
    public string? ContainerState { get; set; }
    public string? ControlHost { get; set; }
    public bool ControlAvailable { get; set; }
    public double ContainerSampledAt { get; set; }
    public bool? ContainerRunning => ContainerState == "running" ? true : ContainerState is "exited" or "created" ? false : null;
    public string Source { get; set; } = "";
    public double CheckedAt { get; set; }
    public double? LatencyMs { get; set; }
}

public sealed class LabAgent
{
    public bool TelemetryStale { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Device { get; set; } = "";
    public string? Model { get; set; }
    public int Sessions { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class LabSession
{
    public bool TelemetryStale { get; set; }
    public string Id { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Device { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Model { get; set; }
    public string State { get; set; } = "Unknown";
    public double UpdatedAt { get; set; }
    public double? InputTokens { get; set; }
    public double? OutputTokens { get; set; }
    public double? CacheReadTokens { get; set; }
    public double? CacheWriteTokens { get; set; }
    public double? TotalTokens { get; set; }
    public double? ContextLimit { get; set; }
    public double? Cost { get; set; }
    public string? CostKind { get; set; }
    public int? ToolCalls { get; set; }
    public string UsageKind { get; set; } = "";
    public string Source { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Workspace { get; set; }
    public bool Open => !TelemetryStale && (State is "Running" or "Open turn" or "Queued");
}

public sealed class LabMonitor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Status { get; set; }
    public double UpdatedAt { get; set; }
    public double? Uptime24h { get; set; }
    public double? LatencyMs { get; set; }
    public List<int?> History { get; set; } = [];
}

public sealed class LabSource
{
    public string Device { get; set; } = "";
    public string Status { get; set; } = "";
    public double SampledAt { get; set; }
    public string Detail { get; set; } = "";
    public List<string> Errors { get; set; } = [];
}
