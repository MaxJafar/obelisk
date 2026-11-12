namespace JarvisHud.Core;

/// <summary>
/// Preview data for inspecting how the orb grows. Everything here is invented and the HUD
/// labels it SIMULATION wherever it is shown; it is never mixed with real Gateway data.
/// </summary>
public static class PreviewSource
{
    /// <param name="growth">0 = a newborn agent, 1 = a mature one wired into the whole homelab.</param>
    public static GrowthSnapshot Build(string agentId, double growth)
    {
        growth = Math.Clamp(growth, 0, 1);

        // Counts grow geometrically so the slider crosses ring thresholds at an even pace.
        int Scale(double min, double max) => (int)Math.Round(min * Math.Pow(max / min, growth));
        int From(double max, double startAt) => growth <= startAt ? 0 : (int)Math.Round(Math.Pow(max, (growth - startAt) / (1 - startAt)));

        var domains = new List<DomainInfo>
        {
            Domain("skills", "Skills", DomainKind.Capability, Scale(2, 96)),
            Domain("tools", "Tools", DomainKind.Capability, Scale(4, 72)),
            Domain("plugins", "Plugins", DomainKind.Capability, From(28, 0.10)),
            Domain("automation", "Automation", DomainKind.Capability, From(40, 0.30)),
            Domain("memory", "Memory", DomainKind.Knowledge, Scale(3, 5200)),
            Domain("sessions", "Sessions", DomainKind.Knowledge, Scale(1, 640)),
            Domain("wiki", "Wiki", DomainKind.Knowledge, From(380, 0.35)),
            Domain("commands", "Node commands", DomainKind.Access, Scale(3, 44)),
            Domain("channels", "Channels", DomainKind.Access, From(6, 0.20)),
        };
        domains.RemoveAll(d => d.Count == 0);

        var deviceCount = 1 + (int)Math.Round(growth * 7);
        string[] platforms = ["windows", "macos", "macos", "linux", "ios", "linux", "android", "windows"];
        var devices = new List<DeviceInfo>();
        for (var i = 0; i < deviceCount; i++)
        {
            var commands = Enumerable.Range(0, 2 + (int)(growth * (4 + i * 3))).Select(c => $"sim.command.{c}").ToList();
            devices.Add(new DeviceInfo(
                Id: $"sim-node-{i + 1:00}",
                Name: $"SIM-NODE-{i + 1:00}",
                Platform: platforms[i % platforms.Length],
                Kind: i == 1 ? "gateway" : "node",
                Online: i != 4,
                IsSelf: i == 0,
                Caps: i % 2 == 0 ? ["screen"] : [],
                Commands: commands));
        }

        return new GrowthSnapshot(
            GrowthSnapshot.SourceSimulated,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new AgentInfo(agentId, GrowthSnapshot.Capitalize(agentId), "simulated"),
            devices,
            domains);
    }

    private static DomainInfo Domain(string id, string label, DomainKind kind, int count) =>
        new(id, label, kind, count, Array.Empty<string>());
}
