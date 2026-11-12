using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisHud.Core;

/// <summary>User preferences. Stored as plain JSON; secrets never go in here (see SecretStore).</summary>
public sealed class Settings
{
    public string GatewayUrl { get; set; } = "";
    public string AgentId { get; set; } = "jarvis";

    /// <summary>Connect to the Gateway on launch. Off until the user links the app once.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Key of the Gateway session the HUD created for its own conversation with the agent.</summary>
    public string? HudSessionKey { get; set; }

    /// <summary>Microphone metering. Off by default; the mic is never opened without this.</summary>
    public bool MicEnabled { get; set; }
    public string? MicDeviceId { get; set; }

    /// <summary>React to what the speakers are actually playing (levels only, nothing is recorded).</summary>
    public bool OutputReactive { get; set; }
    public bool LightDeskReactive { get; set; }
    public bool VoiceEnabled { get; set; } = true;

    /// <summary>Flat voice contour amplitude, 0.25 to 1.5.</summary>
    public double EffectIntensity { get; set; } = 1.0;
    public int FrameRate { get; set; } = 60;
    public bool AmbientMotion { get; set; } = true;

    public bool CompactOverlay { get; set; }
    public bool StartInTray { get; set; }
    public bool HomelabVisible { get; set; } = true;

    /// <summary>Preview mode: draws invented growth so the visuals can be inspected. Always labelled.</summary>
    public bool Simulate { get; set; }
    public double SimulatedGrowth { get; set; } = 0.5;

    /// <summary>Set for preview runs started from the command line, which must not alter saved settings.</summary>
    [JsonIgnore]
    public bool Transient { get; set; }

    [JsonIgnore]
    public static string Directory { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("OBELISK_DATA_DIR") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obelisk"));

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
                if (loaded is not null) return loaded.Normalize();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file should not stop the app from starting: fall back to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        if (Transient) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences are best-effort; the running session keeps its in-memory values.
        }
    }

    private Settings Normalize()
    {
        AgentId = string.IsNullOrWhiteSpace(AgentId) ? "jarvis" : AgentId.Trim();
        GatewayUrl = GatewayUrl?.Trim() ?? "";
        EffectIntensity = Math.Clamp(EffectIntensity, 0.25, 1.5);
        FrameRate = FrameRate <= 30 ? 30 : 60;
        SimulatedGrowth = Math.Clamp(SimulatedGrowth, 0, 1);
        return this;
    }
}
