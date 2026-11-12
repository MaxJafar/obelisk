using System.Diagnostics;
using System.Text.Json;

namespace JarvisHud.Homelab;

public sealed record LabActionResult(bool Ok, string Message, string? State = null);

/// <summary>Explicit user actions only. Observed identities are rechecked at the host before mutation.</summary>
public sealed class LabControlClient : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private int _busy;
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public static bool Available(LabSnapshot snapshot, LabService service, bool preview) =>
        !preview && !snapshot.IsStale && service.ControlAvailable && !string.IsNullOrWhiteSpace(service.ControlHost) &&
        service.ContainerRunning is not null && service.ContainerId?.Length == 64 &&
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - service.ContainerSampledAt is >= 0 and < 120 &&
        snapshot.Devices.Any(d => d.Id == service.ControlHost && d.Online);

    public Task<LabActionResult> SetRunningAsync(LabSnapshot snapshot, LabService service, bool running, bool preview) =>
        ExecuteAsync(snapshot,service,running,preview);
    public Task<LabActionResult> VerifyAsync(LabSnapshot snapshot,LabService service,bool preview) =>
        ExecuteAsync(snapshot,service,null,preview);
    private async Task<LabActionResult> ExecuteAsync(LabSnapshot snapshot, LabService service, bool? running, bool preview)
    {
        if (!Available(snapshot, service, preview)) return new(false, "Fresh service control is unavailable. Refresh the map.");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(false, "Another service action is in progress.");
        try
        {
            var python = HomelabClient.FindPython();
            if (python is null) return new(false, "The local control runtime is unavailable.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var process = new Process { StartInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = new System.Text.UTF8Encoding(false)
            }};
            process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"Homelab","control.py"));
            process.StartInfo.ArgumentList.Add("--config");
            process.StartInfo.ArgumentList.Add(Path.Combine(JarvisHud.Core.Settings.Directory,"homelab.json"));
            process.Start();
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                action=running is null?"inspect":"set", device=service.ControlHost, container=service.Container, containerId=service.ContainerId,
                expectedRunning=service.ContainerRunning, running, sampledAt=service.ContainerSampledAt
            }));
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var json=await output.ConfigureAwait(false); await error.ConfigureAwait(false);
            using var result=JsonDocument.Parse(json);
            var root=result.RootElement;
            return new(root.TryGetProperty("ok",out var ok)&&ok.GetBoolean(),
                root.TryGetProperty("message",out var message)?message.GetString()??"Action completed.":"Action status unknown. Refresh the map.",
                root.TryGetProperty("state",out var state)?state.GetString():null);
        }
        catch (Exception e) when(e is OperationCanceledException or IOException or JsonException or System.ComponentModel.Win32Exception)
        { return new(false,"Action status unknown. Refresh to verify the service state."); }
        finally { Interlocked.Exchange(ref _busy,0); }
    }
    public void Dispose() => _stop.Cancel();
}
