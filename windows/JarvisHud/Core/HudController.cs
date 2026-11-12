using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisHud.Gateway;

namespace JarvisHud.Core;

/// <summary>
/// The app's brain: owns the Gateway link, keeps the growth snapshot current, and derives the
/// agent's state from real Gateway events. It never invents activity: with no link the state is
/// Offline, and Thinking / Executing / Responding only appear when the Gateway reports a run.
/// </summary>
public sealed class HudController : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _activeRuns = [];
    private readonly StringBuilder _reply = new();
    private GatewayClient? _client;
    private CancellationTokenSource _refreshCts = new();
    private Timer? _watchdog;
    private string? _replyRunId;
    private string? _ownRunId;
    private DateTime _errorUntil;
    private volatile OrbTopic _topic;

    public IReadOnlyList<AgentInventoryItem> Agents { get; private set; } = Array.Empty<AgentInventoryItem>();

    public Settings Settings { get; }
    public GrowthSnapshot Snapshot { get; private set; }
    public HudState State { get; private set; } = HudState.Offline;
    public string StateDetail { get; private set; } = "";
    public LinkStatus Link { get; private set; } = LinkStatus.NotLinked;
    public string Subtitle { get; private set; } = "";

    /// <summary>Raised (on any thread) when Snapshot, State, Link or Subtitle changed.</summary>
    public event Action? Changed;
    /// <summary>Raised when a tool call targets a device, so its orb can pulse.</summary>
    public event Action<string>? DevicePulse;
    /// <summary>Real arriving reply text; drives a pulse when no output audio is present.</summary>
    public event Action<int>? ReplyActivity;
    public event Action<string, string, bool>? ReplyUpdated;
    public event Action<string>? ReplyCancelled;

    public bool IsOnline => Link.Phase == LinkPhase.Online;
    public bool IsSimulated => Settings.Simulate;

    /// <summary>The state shown while previewing with simulated data. Not persisted.</summary>
    public HudState PreviewState { get; set; } = HudState.Idle;
    public OrbTopic PreviewTopic { get; set; } = OrbTopic.General;
    public OrbTopic Topic => IsSimulated ? PreviewTopic : _topic;
    public bool HasOwnRun => _ownRunId is not null;

    public HudController(Settings settings)
    {
        Settings = settings;
        Snapshot = BuildOfflineSnapshot();
    }

    public void Start()
    {
        _watchdog = new Timer(_ => ExpireStaleRuns(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        RecomputeState();
        if (Settings.AutoConnect && !string.IsNullOrWhiteSpace(Settings.GatewayUrl))
            LinkGateway();
        else
            Publish();
    }

    // ---- link ---------------------------------------------------------------------------------

    /// <summary>Opens the Gateway link using the address in Settings. Returns an error message, or null.</summary>
    public string? LinkGateway()
    {
        var raw = Settings.GatewayUrl.Trim();
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) raw = "wss://" + raw[8..];
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) raw = "ws://" + raw[7..];
        else if (!raw.Contains("://")) raw = "wss://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return "That is not a valid Gateway address. Use the form wss://host:18789.";

        // Plain ws:// would send the device token in the clear; allow it only on this machine.
        if (url.Scheme == "ws" && !url.IsLoopback)
            return "Use wss:// for a Gateway on another machine. Unencrypted ws:// is only allowed on localhost.";

        Unlink(keepAutoConnect: true);

        var client = new GatewayClient(url, DeviceIdentity.LoadOrCreate());
        client.StatusChanged += OnLinkStatus;
        client.Connected += _ => RefreshSoon(TimeSpan.Zero);
        client.EventReceived += OnGatewayEvent;
        lock (_gate) _client = client;
        client.Start();

        Settings.GatewayUrl = url.ToString().TrimEnd('/');
        Settings.AutoConnect = true;
        Settings.Save();
        return null;
    }

    public void Unlink(bool keepAutoConnect = false)
    {
        GatewayClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _activeRuns.Clear();
            _ownRunId = null;
        }
        if (client is not null)
        {
            client.StatusChanged -= OnLinkStatus;
            client.EventReceived -= OnGatewayEvent;
            client.Dispose();
        }
        if (!keepAutoConnect)
        {
            Settings.AutoConnect = false;
            Settings.Save();
        }
        Link = LinkStatus.NotLinked;
        Agents = Array.Empty<AgentInventoryItem>();
        Snapshot = BuildOfflineSnapshot();
        RecomputeState();
        Publish();
    }

    private void OnLinkStatus(LinkStatus status)
    {
        Link = status;
        if (status.Phase != LinkPhase.Online)
        {
            lock (_gate)
            {
                _activeRuns.Clear();
                _ownRunId = null;
            }
            if (!Settings.Simulate) { Snapshot = BuildOfflineSnapshot(); Agents = Array.Empty<AgentInventoryItem>(); }
        }
        RecomputeState();
        Publish();
    }

    // ---- snapshot -----------------------------------------------------------------------------

    private GrowthSnapshot BuildOfflineSnapshot() => Settings.Simulate
        ? PreviewSource.Build(Settings.AgentId, Settings.SimulatedGrowth)
        : GrowthSnapshot.Empty(Settings.AgentId);

    /// <summary>Re-reads settings that change the data source (preview on/off, growth, agent id).</summary>
    public void ReloadSource()
    {
        if (Settings.Simulate || !IsOnline)
        {
            Snapshot = BuildOfflineSnapshot();
            RecomputeState();
            Publish();
        }
        else
        {
            RefreshSoon(TimeSpan.Zero);
        }
    }

    /// <summary>Coalesces bursts of change events into one refresh.</summary>
    private void RefreshSoon(TimeSpan delay)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _refreshCts.Cancel();
            _refreshCts = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                GatewayClient? client;
                lock (_gate) client = _client;
                if (client is null || !client.IsOnline || Settings.Simulate) return;
                var snapshot = await SnapshotBuilder.BuildAsync(client, Settings.AgentId, cts.Token);
                if (cts.IsCancellationRequested) return;
                var agents = await SnapshotBuilder.ReadInventoryAsync(client, cts.Token);
                if (cts.IsCancellationRequested) return;
                Snapshot = snapshot;
                Agents = agents;
                Publish();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or JsonException)
            {
                // The link dropped mid-refresh; the next connect refreshes again.
            }
        });
    }

    // ---- events → state -------------------------------------------------------------------------

    private void OnGatewayEvent(string name, JsonElement payload)
    {
        if (Settings.Simulate) return;
        switch (name)
        {
            case "presence":
            case "skills.changed":
            case "plugins.changed":
            case "config.changed":
            case "cron":
            case "node.pair.resolved":
            case "device.pair.resolved":
            case SnapshotBuilder.PluginChangedEvent:
                RefreshSoon(TimeSpan.FromMilliseconds(600));
                break;
            case "sessions.changed":
                // Sessions churn constantly while the agent talks; refresh at a relaxed pace.
                RefreshSoon(TimeSpan.FromSeconds(5));
                break;
            case "agent":
                OnAgentEvent(payload);
                break;
            case "chat":
                OnChatEvent(payload);
                break;
        }
    }

    private bool IsOurAgent(JsonElement payload)
    {
        if (Str(payload, "agentId") is { } agentId) return agentId == Settings.AgentId;
        return Str(payload, "sessionKey") is { } key && key.StartsWith($"agent:{Settings.AgentId}:", StringComparison.Ordinal);
    }

    /// <summary>Run lifecycle and tool activity, for any session of this agent.</summary>
    private void OnAgentEvent(JsonElement payload)
    {
        if (!IsOurAgent(payload) || Str(payload, "runId") is not { } runId) return;
        var stream = Str(payload, "stream");
        var data = payload.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;

        if (stream == "lifecycle")
        {
            switch (Str(data, "phase"))
            {
                case "start":
                    lock (_gate) _activeRuns[runId] = DateTime.UtcNow;
                    if (runId != _ownRunId) _topic = OrbTopic.General;
                    SetState(HudState.Thinking, "");
                    return;
                case "end":
                    EndRun(runId, failed: false);
                    return;
                case "error":
                    EndRun(runId, failed: true);
                    return;
            }
            return;
        }

        if (stream == "tool")
        {
            lock (_gate) _activeRuns[runId] = DateTime.UtcNow;
            var tool = Str(data, "name") ?? "";
            switch (Str(data, "phase"))
            {
                case "start":
                    _topic = OrbExpression.TopicForTool(tool, _topic);
                    SetState(HudState.Executing, tool);
                    PulseTargetDevice(data);
                    break;
                case "result":
                    SetState(HudState.Thinking, "");
                    break;
            }
        }
    }

    /// <summary>Tool arguments that name a node make that device's orb pulse.</summary>
    private void PulseTargetDevice(JsonElement toolData)
    {
        if (!toolData.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) return;
        var target = Str(args, "node") ?? Str(args, "nodeId") ?? Str(args, "host");
        if (target is null) return;
        var device = Snapshot.Devices.FirstOrDefault(dev =>
            string.Equals(dev.Id, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dev.Name, target, StringComparison.OrdinalIgnoreCase));
        if (device is not null) DevicePulse?.Invoke(device.Id);
    }

    /// <summary>Streaming reply text. Text is only shown for the HUD's own session.</summary>
    private void OnChatEvent(JsonElement payload)
    {
        if (!IsOurAgent(payload) || Str(payload, "runId") is not { } runId) return;
        var own = Str(payload, "sessionKey") == Settings.HudSessionKey || runId == _ownRunId;

        switch (Str(payload, "state"))
        {
            case "delta":
                lock (_gate) _activeRuns[runId] = DateTime.UtcNow;
                if (IsOnline && Str(payload, "deltaText") is { Length: > 0 } arriving)
                    ReplyActivity?.Invoke(arriving.Length);
                if (own)
                {
                    if (_replyRunId != runId)
                    {
                        _replyRunId = runId;
                        _reply.Clear();
                    }
                    if (payload.TryGetProperty("replace", out var replace) && replace.ValueKind == JsonValueKind.True) _reply.Clear();
                    // The first frame can carry a full snapshot in `message` plus the same text as a
                    // delta; only the delta is appended so nothing is doubled.
                    if (Str(payload, "deltaText") is { Length: > 0 } delta) _reply.Append(delta);
                    else if (ExtractText(payload) is { Length: > 0 } snapshot) { _reply.Clear(); _reply.Append(snapshot); }
                    Subtitle = Tail(_reply.ToString());
                    ReplyUpdated?.Invoke(runId, _reply.ToString(), false);
                }
                // Republish on every own delta so the subtitle keeps up; other sessions only flip the state.
                if (IsOnline && (own || State != HudState.Speaking))
                {
                    State = HudState.Speaking;
                    StateDetail = "";
                    Publish();
                }
                break;

            case "final":
                if (own)
                {
                    var final = ExtractText(payload) ?? _reply.ToString();
                    if (final.Length > 0) { Subtitle = Tail(final); ReplyUpdated?.Invoke(runId, final, true); }
                }
                EndRun(runId, failed: false);
                break;

            case "aborted":
                if (own) { Subtitle = "Stopped."; ReplyCancelled?.Invoke(runId); }
                EndRun(runId, failed: false);
                break;

            case "error":
                if (own) ReplyCancelled?.Invoke(runId);
                if (own) Subtitle = "The agent reported an error: " + (Str(payload, "errorMessage") ?? Str(payload, "errorKind") ?? "unknown") + ".";
                EndRun(runId, failed: true);
                break;
        }
    }

    private void EndRun(string runId, bool failed)
    {
        lock (_gate)
        {
            _activeRuns.Remove(runId);
            if (_ownRunId == runId) _ownRunId = null;
            if (failed) _errorUntil = DateTime.UtcNow.AddSeconds(5);
        }
        RecomputeState();
        Publish();
    }

    private void ExpireStaleRuns()
    {
        bool changed;
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-3);
            var stale = _activeRuns.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (var id in stale) _activeRuns.Remove(id);
            changed = stale.Count > 0 || (_errorUntil != default && DateTime.UtcNow > _errorUntil && State == HudState.Error);
        }
        if (!changed) return;
        RecomputeState();
        Publish();
    }

    private void SetState(HudState state, string detail)
    {
        if (!IsOnline) return;
        if (State == state && StateDetail == detail) return;
        State = state;
        StateDetail = detail;
        Publish();
    }

    private void RecomputeState()
    {
        bool running, errored;
        lock (_gate)
        {
            running = _activeRuns.Count > 0;
            errored = DateTime.UtcNow < _errorUntil;
        }

        if (Settings.Simulate || !IsOnline)
        {
            // Preview mode shows a chosen state so each look can be judged; the banner says it is simulated.
            State = Settings.Simulate ? PreviewState : HudState.Offline;
            StateDetail = "";
            return;
        }
        if (errored) State = HudState.Error;
        else if (!running) State = HudState.Idle;
        else if (State is HudState.Offline or HudState.Idle or HudState.Error) State = HudState.Thinking;
        if (!running) StateDetail = "";
    }

    // ---- chat -----------------------------------------------------------------------------------

    /// <summary>Sends a message to the agent in the HUD's own session. Replies stay on the Gateway link.</summary>
    public async Task SendAsync(string text)
    {
        if (HasOwnRun) throw new InvalidOperationException("A reply is already running. Stop it or wait for completion.");
        GatewayClient? client;
        lock (_gate) client = _client;
        if (client is null || !client.IsOnline) throw new InvalidOperationException("The Gateway link is not online.");

        var sessionKey = await EnsureSessionAsync(client);
        var runId = "hud-" + Guid.NewGuid().ToString("N");
        _ownRunId = runId;
        _reply.Clear();
        _replyRunId = runId;
        Subtitle = "";
        _topic = OrbExpression.TopicForText(text);
        lock (_gate) _activeRuns[runId] = DateTime.UtcNow;
        SetState(HudState.Thinking, "");

        try
        {
            // No `deliver` and no originating* fields: the Gateway then routes the reply to the
            // internal webchat channel only, never to Telegram or any other external channel.
            var accepted = await client.CallAsync("chat.send", new JsonObject
            {
                ["sessionKey"] = sessionKey,
                ["message"] = text,
                // Voice/action mode: never spend a turn budget on extended thinking.
                ["thinking"] = "off",
                ["idempotencyKey"] = runId,
            });
            if (Str(accepted, "runId") is { } actual && actual != runId && _ownRunId == runId)
            {
                lock (_gate) { _activeRuns.Remove(runId); _activeRuns[actual] = DateTime.UtcNow; _ownRunId = actual; }
            }
        }
        catch
        {
            EndRun(runId, failed: true);
            throw;
        }
    }

    /// <summary>Stops the HUD's own run.</summary>
    public async Task StopAsync()
    {
        GatewayClient? client;
        lock (_gate) client = _client;
        if (client is null || !client.IsOnline || Settings.HudSessionKey is not { Length: > 0 } key) return;
        var parameters = new JsonObject { ["sessionKey"] = key };
        if (_ownRunId is { } runId) parameters["runId"] = runId;
        await client.CallAsync("chat.abort", parameters);
    }

    /// <summary>
    /// The HUD talks in its own session rather than the agent's main one, which is the same
    /// rolling conversation the owner's Telegram DMs land in.
    /// </summary>
    private async Task<string> EnsureSessionAsync(GatewayClient client)
    {
        if (Settings.HudSessionKey is { Length: > 0 } existing &&
            existing.StartsWith($"agent:{Settings.AgentId}:", StringComparison.Ordinal))
            return existing;

        // Recover the existing HUD conversation if settings were reset or never saved.
        // Fixed labels are unique in the Gateway; repeated create attempts must not hide the chat.
        var listed = await client.CallAsync("sessions.list", new JsonObject
        {
            ["agentId"] = Settings.AgentId,
            ["limit"] = 100,
        });
        if (listed.TryGetProperty("sessions", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (Str(row, "label") == "Jarvis HUD" && Str(row, "key") is { } recovered &&
                    recovered.StartsWith($"agent:{Settings.AgentId}:", StringComparison.Ordinal))
                {
                    Settings.HudSessionKey = recovered;
                    Settings.Save();
                    return recovered;
                }
            }
        }

        var created = await client.CallAsync("sessions.create", new JsonObject
        {
            ["agentId"] = Settings.AgentId,
            ["label"] = "Jarvis HUD",
            ["thinkingLevel"] = "off",
        });
        var key = Str(created, "key") ?? throw new InvalidOperationException("The Gateway did not return a session key.");
        Settings.HudSessionKey = key;
        Settings.Save();
        return key;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static string? ExtractText(JsonElement payload)
    {
        if (!payload.TryGetProperty("message", out var message)) return null;
        if (message.ValueKind == JsonValueKind.String) return message.GetString();
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (Str(message, "text") is { } text) return text;
        if (!message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (Str(block, "type") == "text" && Str(block, "text") is { } part) sb.Append(part);
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>Keep the full reply available in the scrollable chat, including long streamed responses.</summary>
    private static string Tail(string text)
    {
        return text.Trim();
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private void Publish() => Changed?.Invoke();

    public void Dispose()
    {
        _watchdog?.Dispose();
        _refreshCts.Cancel();
        GatewayClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }
        client?.Dispose();
    }
}
