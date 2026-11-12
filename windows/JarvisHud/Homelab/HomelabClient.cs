using System.Diagnostics;
using System.Text.Json;
using JarvisHud.Core;

namespace JarvisHud.Homelab;

/// <summary>One background collection at a time. No polling while hidden, no work on the UI thread.</summary>
public sealed class HomelabClient : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _single = new(1, 1);
    private readonly bool _readOnlyPreview;
    private volatile bool _visible;
    private volatile bool _dashboard;
    public bool PollingEnabled { get => _polling; set { _polling = value; if(value) _next=DateTime.MinValue; } }
    private volatile bool _polling = true;
    public bool IsPreview => _readOnlyPreview;
    private DateTime _next = DateTime.MinValue;
    private static string Cache => Path.Combine(Settings.Directory, "homelab-snapshot.json");
    public LabSnapshot Snapshot { get; private set; } = new();
    public string Status { get; private set; } = "Connecting to homelab";
    public bool Refreshing { get; private set; }
    public event Action? Changed;

    public HomelabClient(bool preview)
    {
        _readOnlyPreview = preview;
        try
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "--homelab-snapshot");
            var path = preview ? Path.Combine(AppContext.BaseDirectory,"Homelab","demo-snapshot.json") : Cache;
            if (File.Exists(path)) { Snapshot = LabSnapshot.Parse(File.ReadAllText(path)); if(preview)Snapshot.Timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds(); Status = "Cached snapshot · awaiting refresh"; }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        if (preview) Status = "Snapshot preview · live polling disabled";
        else _ = Task.Run(LoopAsync);
    }

    public void SetActivity(bool visible, bool dashboard)
    {
        if (visible && (!_visible || dashboard && !_dashboard)) _next = DateTime.MinValue;
        _visible = visible; _dashboard = dashboard;
    }

    private async Task LoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(_stop.Token))
                if (_polling && _visible && DateTime.UtcNow >= _next) await RefreshAsync();
        }
        catch (OperationCanceledException) { }
    }

    public async Task RefreshAsync()
    {
        if (_readOnlyPreview || !await _single.WaitAsync(0)) return;
        try
        {
            Refreshing = true; Status = "Refreshing devices, services and session metadata…"; Changed?.Invoke();
            var python = FindPython();
            if (python is null) throw new IOException("Python runtime unavailable");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            using var process = new Process { StartInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            }};
            process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Homelab", "collector.py"));
            process.StartInfo.ArgumentList.Add("--config");
            process.StartInfo.ArgumentList.Add(Path.Combine(Settings.Directory,"homelab.json"));
            process.Start();
            // The headless bridge owns an input pipe. A collector must receive EOF,
            // not inherit or contend with the application's command input.
            process.StandardInput.Close();
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var json = await stdout.ConfigureAwait(false); await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Collector could not complete");
            var snapshot = LabSnapshot.Parse(json);
            snapshot.KeepUnavailableInventory(Snapshot);
            Snapshot = snapshot;
            Status = $"Updated {DateTime.Now:HH:mm:ss} · {snapshot.CollectionMs / 1000d:0.0}s collection · {(_dashboard ? "30" : "90")}s refresh";
            Directory.CreateDirectory(Settings.Directory);
            await File.WriteAllTextAsync(Cache + ".tmp", JsonSerializer.Serialize(snapshot, LabSnapshot.Json), _stop.Token);
            File.Move(Cache + ".tmp", Cache, overwrite: true);
        }
        catch (OperationCanceledException) { Status = "Refresh timed out · showing last snapshot"; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status = e.Message + " · showing last snapshot"; }
        finally
        {
            _next = DateTime.UtcNow.AddSeconds(_dashboard ? 30 : 90);
            Refreshing = false; _single.Release(); Changed?.Invoke();
        }
    }

    internal static string? FindPython()
    {
        var configured=Environment.GetEnvironmentVariable("OBELISK_PYTHON");
        if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))return configured;
        foreach(var directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
        {
            var path=Path.Combine(directory,"python.exe");
            if(File.Exists(path)&&!path.Contains("WindowsApps",StringComparison.OrdinalIgnoreCase))return path;
        }
        return null;
    }

    public void Dispose() => _stop.Cancel();
}
