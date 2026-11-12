using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisHud.Core;

namespace JarvisHud.Gateway;

public enum LinkPhase
{
    NotLinked,
    Connecting,
    PairingRequired,
    Online,
    AuthFailed,
    Unreachable,
}

/// <summary>Where the Gateway link stands, in words the HUD can show as-is.</summary>
public sealed record LinkStatus(LinkPhase Phase, string Summary, string Detail)
{
    public static readonly LinkStatus NotLinked = new(LinkPhase.NotLinked, "NOT LINKED",
        "Not linked. Linking sends a pairing request the Gateway owner must approve.");
}

public sealed class GatewayException(string code, string message, JsonElement? details = null, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;
    public JsonElement? Details { get; } = details;
    public bool Retryable { get; } = retryable;

    /// <summary>The structured code under error.details.code, e.g. PAIRING_REQUIRED.</summary>
    public string? DetailCode =>
        Details is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    public string? DetailString(string name) =>
        Details is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// An operator client for the OpenClaw Gateway WebSocket protocol (wire version 4).
/// Implements the documented handshake: wait for connect.challenge, sign it with the device
/// identity, send connect, read hello-ok. Then request/response RPC plus server events, with
/// reconnect and back-off. See GATEWAY-NOTES.md for the sources of every field used here.
/// </summary>
public sealed class GatewayClient : IDisposable
{
    // The Gateway only accepts client ids and modes from a closed registry
    // (packages/gateway-protocol/src/client-info.ts). There is no Windows desktop id, so this
    // app identifies as the generic gateway client in UI mode.
    public const string ClientId = "gateway-client";
    public const string ClientMode = "ui";
    public const string Platform = "windows";
    public const string DeviceFamily = "Windows";
    private const int ProtocolVersion = 4;

    private static readonly string[] RequestedScopes = ["operator.read", "operator.write"];
    private static readonly string[] Caps = ["tool-events"];

    private readonly Uri _url;
    private readonly DeviceIdentity _identity;
    private readonly string _tokenKey;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private ClientWebSocket? _socket;
    private TaskCompletionSource<JsonElement>? _challenge;
    private Task? _loop;
    private long _nextId;

    public event Action<LinkStatus>? StatusChanged;
    /// <summary>Raised for every server event after the handshake: event name and payload.</summary>
    public event Action<string, JsonElement>? EventReceived;
    /// <summary>Raised with the hello-ok payload each time a connection is established.</summary>
    public event Action<JsonElement>? Connected;

    public LinkStatus Status { get; private set; } = LinkStatus.NotLinked;
    public IReadOnlySet<string> Methods { get; private set; } = new HashSet<string>();
    public IReadOnlyList<string> GrantedScopes { get; private set; } = [];
    public string? ServerVersion { get; private set; }
    public bool IsOnline => Status.Phase == LinkPhase.Online;
    public string DeviceId => _identity.DeviceId;

    public GatewayClient(Uri url, DeviceIdentity identity)
    {
        _url = url;
        _identity = identity;
        // One stored device token per Gateway address.
        _tokenKey = "device-token-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url.ToString().ToLowerInvariant())))[..16].ToLowerInvariant();
    }

    public const string SharedTokenSecret = "gateway-shared-token";

    public void Start() => _loop ??= Task.Run(RunAsync);

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _socket?.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
        FailPending(new OperationCanceledException("Gateway link closed."));
    }

    // ---- connection loop --------------------------------------------------------------------

    private async Task RunAsync()
    {
        var attempt = 0;
        while (!_stop.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                SetStatus(LinkPhase.Connecting, "CONNECTING", $"Connecting to {_url.Host}…");
                await ConnectAndPumpAsync(_stop.Token);
                // A clean close after a successful session: reconnect promptly.
                attempt = 0;
                wait = TimeSpan.FromSeconds(2);
                SetStatus(LinkPhase.Unreachable, "LINK LOST", "The Gateway closed the connection. Reconnecting…");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (GatewayException e)
            {
                if (!HandleConnectError(e, out wait)) break;
            }
            catch (Exception e) when (e is WebSocketException or IOException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                attempt++;
                wait = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(1.7, Math.Min(attempt, 6))));
                SetStatus(LinkPhase.Unreachable, "UNREACHABLE",
                    $"Cannot reach {_url.Host}: {Describe(e)}. Retrying in {wait.TotalSeconds:0}s.");
            }

            FailPending(new IOException("Gateway link is down."));
            try
            {
                await Task.Delay(wait, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Maps a rejected handshake to a status. Returns false when retrying is pointless.</summary>
    private bool HandleConnectError(GatewayException e, out TimeSpan wait)
    {
        wait = TimeSpan.FromSeconds(8);
        var detail = e.DetailCode ?? e.Code;

        if (detail == "PAIRING_REQUIRED")
        {
            var requestId = e.DetailString("requestId");
            var approve = requestId is null
                ? "On the Gateway host run: openclaw devices list, then openclaw devices approve <requestId>."
                : $"On the Gateway host run: openclaw devices approve {requestId}";
            SetStatus(LinkPhase.PairingRequired, "PAIRING REQUIRED",
                $"Waiting for approval of this device ({_identity.DeviceId[..12]}…). {approve}");
            // Keep knocking: the docs say to retry with the same identity until approved.
            return true;
        }

        if (detail is "AUTH_DEVICE_TOKEN_MISMATCH" or "AUTH_SCOPE_MISMATCH")
        {
            // The stored device token no longer matches what the Gateway has; drop it so the next
            // attempt goes back through pairing instead of failing forever.
            SecretStore.Delete(_tokenKey);
            SetStatus(LinkPhase.AuthFailed, "RE-PAIR NEEDED",
                "The Gateway no longer accepts this device's token. Press LINK to request pairing again.");
            return false;
        }

        if (detail.StartsWith("AUTH_", StringComparison.Ordinal) || detail.StartsWith("DEVICE_", StringComparison.Ordinal) ||
            detail is "OPERATOR_ACCESS_DENIED" or "PROTOCOL_MISMATCH" or "CLIENT_VERSION_MISMATCH")
        {
            SetStatus(LinkPhase.AuthFailed, "ACCESS DENIED", $"The Gateway refused the link: {e.Message} ({detail}).");
            return false;
        }

        if (e.Retryable || e.Code == "UNAVAILABLE")
        {
            SetStatus(LinkPhase.Unreachable, "GATEWAY BUSY", $"The Gateway is not ready: {e.Message}. Retrying.");
            wait = TimeSpan.FromSeconds(4);
            return true;
        }

        SetStatus(LinkPhase.AuthFailed, "LINK REJECTED", $"The Gateway refused the link: {e.Message} ({detail}).");
        return false;
    }

    private async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket = socket;
        var challenge = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _challenge = challenge;

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            await socket.ConnectAsync(_url, connectTimeout.Token);
        }

        var pump = PumpAsync(socket, ct);

        // 1. The server speaks first.
        var challengePayload = await challenge.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var nonce = challengePayload.GetProperty("nonce").GetString()
                    ?? throw new InvalidOperationException("connect.challenge carried no nonce.");
        if (!challengePayload.TryGetProperty("ts", out var tsEl) || !tsEl.TryGetInt64(out var signedAt) || signedAt < 0)
            throw new InvalidOperationException("connect.challenge carried no valid ts.");

        // 2. Choose the credential. A device token minted by an earlier pairing wins; otherwise the
        //    shared Gateway token if the user supplied one; otherwise none (identity-bearing
        //    transports such as Tailscale Serve authenticate from headers).
        var deviceToken = SecretStore.ReadString(_tokenKey);
        var sharedToken = deviceToken is null ? SecretStore.ReadString(SharedTokenSecret) : null;
        var signatureToken = sharedToken ?? deviceToken;

        var auth = new JsonObject();
        if (sharedToken is not null) auth["token"] = sharedToken;
        if (deviceToken is not null) auth["deviceToken"] = deviceToken;

        var signature = _identity.SignConnect(ClientId, ClientMode, "operator", RequestedScopes,
            signedAt, signatureToken, nonce, Platform, DeviceFamily);

        var version = typeof(GatewayClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var connect = new JsonObject
        {
            ["minProtocol"] = ProtocolVersion,
            ["maxProtocol"] = ProtocolVersion,
            ["client"] = new JsonObject
            {
                ["id"] = ClientId,
                ["displayName"] = "Jarvis HUD",
                ["version"] = version,
                ["platform"] = Platform,
                ["deviceFamily"] = DeviceFamily,
                ["mode"] = ClientMode,
                ["instanceId"] = _identity.DeviceId[..16],
            },
            ["role"] = "operator",
            ["scopes"] = new JsonArray(RequestedScopes.Select(s => (JsonNode)s).ToArray()),
            ["caps"] = new JsonArray(Caps.Select(s => (JsonNode)s).ToArray()),
            ["commands"] = new JsonArray(),
            ["permissions"] = new JsonObject(),
            ["locale"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
            ["userAgent"] = $"jarvis-hud/{version}",
            ["device"] = new JsonObject
            {
                ["id"] = _identity.DeviceId,
                ["publicKey"] = _identity.PublicKeyBase64Url,
                ["signature"] = signature,
                ["signedAt"] = signedAt,
                ["nonce"] = nonce,
            },
        };
        if (auth.Count > 0) connect["auth"] = auth;

        // 3. hello-ok, or a structured rejection (thrown as GatewayException).
        var hello = await CallCoreAsync(socket, "connect", connect, TimeSpan.FromSeconds(20), ct);
        ApplyHello(hello);
        SetStatus(LinkPhase.Online, "ONLINE", $"Linked to {_url.Host} as an operator ({string.Join(", ", GrantedScopes)}).");
        Connected?.Invoke(hello);

        await pump;
    }

    private void ApplyHello(JsonElement hello)
    {
        if (hello.TryGetProperty("server", out var server) && server.TryGetProperty("version", out var v))
            ServerVersion = v.GetString();

        var methods = new HashSet<string>(StringComparer.Ordinal);
        if (hello.TryGetProperty("features", out var features) && features.TryGetProperty("methods", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var item in m.EnumerateArray())
                if (item.GetString() is { } name) methods.Add(name);
        Methods = methods;

        if (hello.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            if (auth.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
                GrantedScopes = scopes.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => s.Length > 0).ToList();

            // Pairing mints a per-device token: keep it, and stop holding the shared token.
            if (auth.TryGetProperty("deviceToken", out var token) && token.GetString() is { Length: > 0 } deviceToken)
            {
                SecretStore.WriteString(_tokenKey, deviceToken);
                SecretStore.Delete(SharedTokenSecret);
            }
        }
    }

    private async Task PumpAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            JsonElement frame;
            try
            {
                using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                frame = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            Dispatch(frame);
        }
    }

    private void Dispatch(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("type", out var typeEl)) return;
        switch (typeEl.GetString())
        {
            case "event":
            {
                var name = frame.TryGetProperty("event", out var e) ? e.GetString() : null;
                if (name is null) return;
                var payload = frame.TryGetProperty("payload", out var p) ? p : default;
                if (name == "connect.challenge")
                {
                    _challenge?.TrySetResult(payload);
                    return;
                }
                try
                {
                    EventReceived?.Invoke(name, payload);
                }
                catch (Exception)
                {
                    // A faulty event handler must not take the socket down with it.
                }
                break;
            }
            case "res":
            {
                var id = frame.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (id is null || !_pending.TryRemove(id, out var tcs)) return;
                if (frame.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    tcs.TrySetResult(frame.TryGetProperty("payload", out var payload) ? payload : default);
                }
                else
                {
                    var error = frame.TryGetProperty("error", out var err) ? err : default;
                    string Field(string n, string fallback) =>
                        error.ValueKind == JsonValueKind.Object && error.TryGetProperty(n, out var f) && f.ValueKind == JsonValueKind.String
                            ? f.GetString()!
                            : fallback;
                    JsonElement? details = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("details", out var d) ? d : null;
                    var retryable = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("retryable", out var r) && r.ValueKind == JsonValueKind.True;
                    tcs.TrySetException(new GatewayException(Field("code", "ERROR"), Field("message", "Request failed."), details, retryable));
                }
                break;
            }
        }
    }

    // ---- RPC ----------------------------------------------------------------------------------

    /// <summary>Calls a Gateway method and returns its payload. Throws GatewayException on an error response.</summary>
    public Task<JsonElement> CallAsync(string method, JsonNode? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null || !IsOnline) throw new InvalidOperationException("The Gateway link is not online.");
        return CallCoreAsync(socket, method, parameters ?? new JsonObject(), timeout ?? TimeSpan.FromSeconds(30), ct);
    }

    public bool Supports(string method) => Methods.Contains(method);

    private async Task<JsonElement> CallCoreAsync(ClientWebSocket socket, string method, JsonNode parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = "hud-" + Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var frame = new JsonObject
            {
                ["type"] = "req",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            };
            var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
            await _sendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(error);
        _challenge?.TrySetException(error);
    }

    private void SetStatus(LinkPhase phase, string summary, string detail)
    {
        var status = new LinkStatus(phase, summary, detail);
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private static string Describe(Exception e) => e switch
    {
        TimeoutException or OperationCanceledException => "timed out",
        WebSocketException w when w.InnerException is not null => w.InnerException.Message,
        _ => e.Message,
    };
}
