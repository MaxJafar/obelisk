using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JarvisHud.Audio;

/// <summary>Owns the Go supervisor; its stdin lease and Windows job own native CUDA inference.</summary>
public sealed class LocalAudioEngine : IDisposable
{
    internal static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("OBELISK_AUDIO_DIR") ?? Path.Combine(JarvisHud.Core.Settings.Directory, "audio"));
    public static bool Installed => File.Exists(Path.Combine(Root,"models","Qwen3-TTS-12Hz-1.7B-Base-Q8_0.gguf")) &&
        File.Exists(Path.Combine(Root,"models","mmproj-Qwen3-TTS-12Hz-1.7B-Base-bf16.gguf")) &&
        File.Exists(Path.Combine(Root,"runtime","llama-server.exe")) &&
        File.Exists(Path.Combine(Root,"voices","jarvis-en.wav")) && File.Exists(Path.Combine(Root,"voices","jarvis-ru.wav"));
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(75) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Process? _owned;
    public string Status { get; private set; } = Installed ? "Local Qwen · ready on demand" : "Local voice needs setup";
    private string Token => File.ReadAllText(Path.Combine(Root, "data", "engine.token")).Trim();

    private HttpRequestMessage Request(string path, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, "http://127.0.0.1:3920" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }
    public async Task PrepareAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = linked.Token;
        await _gate.WaitAsync(ct);
        try
        {
            if(!Installed)throw new IOException("Install the local audio runtime and add your English/Russian reference voices. See Settings.");
            var reachable = false;
            try
            {
                using var request = Request("/health", HttpMethod.Get);
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) throw new IOException("Local audio endpoint rejected its credential.");
                reachable = true;
            }
            catch (Exception e) when (e is HttpRequestException or FileNotFoundException or DirectoryNotFoundException) { }
            if (!reachable)
            {
                var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "obelisk-audio.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
                info.ArgumentList.Add("--owned");
                info.ArgumentList.Add("--root"); info.ArgumentList.Add(Root);
                _owned?.Dispose();
                _owned = Process.Start(info) ?? throw new IOException("Could not start the local audio engine.");
                _ = _owned.StandardOutput.ReadToEndAsync(); _ = _owned.StandardError.ReadToEndAsync();
                for (var i = 0; i < 50; i++)
                {
                    await Task.Delay(100, ct);
                    if (_owned.HasExited) throw new IOException("Local audio engine exited during startup.");
                    try
                    {
                        using var probe = Request("/health", HttpMethod.Get);
                        using var result = await _http.SendAsync(probe, ct);
                        if (result.IsSuccessStatusCode) { reachable = true; break; }
                    }
                    catch (Exception e) when (e is HttpRequestException or FileNotFoundException) { }
                }
                if (!reachable) throw new IOException("Local audio engine did not respond.");
            }
            Status = "Warming local voice";
            using var warm = Request("/warmup", HttpMethod.Post);
            using var ready = await _http.SendAsync(warm, ct);
            if (!ready.IsSuccessStatusCode) throw new IOException("Local voice model could not load. The text reply remains available.");
            Status = "Qwen · local GPU · English / Russian";
        }
        catch { Status = "Local voice unavailable"; throw; }
        finally { _gate.Release(); }
    }
    public async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct)
    {
        await PrepareAsync(ct);
        using var request = Request("/v1/audio/speech", HttpMethod.Post);
        request.Content = new StringContent(JsonSerializer.Serialize(new { input = text, voice = "jarvis-auto", response_format = "pcm" }), Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new IOException($"Local speech failed: HTTP {status}."); }
        var format = response.Content.Headers.ContentType?.ToString() ?? "";
        if (!format.Contains("float", StringComparison.OrdinalIgnoreCase) || !format.Contains("24000")) { response.Dispose(); throw new IOException("Unsupported audio format from the local engine."); }
        return new SpeechAudio(response, await response.Content.ReadAsStreamAsync(ct), ct);
    }
    public void Dispose()
    {
        _stop.Cancel();
        try { _owned?.StandardInput.Close(); } catch (InvalidOperationException) { }
        _owned?.Dispose(); _http.Dispose();
    }
}
