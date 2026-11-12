using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace JarvisHud.Audio;

/// <summary>Explicit one-turn speech client. Credentials stay in the existing protected backend file.</summary>
public sealed class SpeechClient : IDisposable
{
    public event Action<float, float[]>? Features;
    public event Action<bool>? PlaybackChanged;
    private readonly LocalAudioEngine _local = new();
    public string EngineStatus => _local.Status;
    public long LastUnderrunMs { get; private set; }
    public Task PrepareAsync(CancellationToken ct) => _local.PrepareAsync(ct);
    public Task<SpeechAudio> OpenSynthesisAsync(string text, CancellationToken ct) =>
        _local.SynthesizeAsync(System.Text.RegularExpressions.Regex.Replace(text, @"[*#`]", "").Trim(), ct);
    public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) =>
        throw new IOException("Set your transcription API key in Settings before using voice input.");

    public async Task PlayAsync(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length == 0) return;
        ct.ThrowIfCancellationRequested();
        using var memory = new MemoryStream(bytes);
        using var reader = new WaveFileReader(memory);
        var spectrum = new AudioSpectrum();
        var source = reader.ToSampleProvider();
        var channels = source.WaveFormat.Channels;
        var frameSamples = Math.Max(1, source.WaveFormat.SampleRate / 50);
        var samples = new float[frameSamples * channels];
        var mono = new float[frameSamples];
        var timeline = new List<AudioFeatures>();
        int read;
        while ((read = source.Read(samples.AsSpan())) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var frames = read / channels;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += samples[i * channels + c];
                mono[i] = sum / channels;
            }
            timeline.Add(spectrum.Process(mono.AsSpan(0, frames), source.WaveFormat.SampleRate));
        }
        reader.Position = 0;
        using var player = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 60);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) => { if (e.Exception is not null) done.TrySetException(e.Exception); else done.TrySetResult(); };
        player.Init(reader);
        try
        {
            PlaybackChanged?.Invoke(true);
            player.Play();
            while (!done.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                // Hardware playback position, not reader lookahead or unrelated desktop sound.
                var seconds = player.GetPosition() / (double)player.OutputWaveFormat.AverageBytesPerSecond;
                var index = (int)(seconds * source.WaveFormat.SampleRate / frameSamples);
                if (index >= 0 && index < timeline.Count)
                {
                    var frame = timeline[index];
                    Features?.Invoke(frame.Level, frame.Bands);
                }
                await Task.WhenAny(done.Task, Task.Delay(20, ct));
            }
            await done.Task;
        }
        finally { player.Stop(); Features?.Invoke(0f, new float[AudioSpectrum.BandCount]); PlaybackChanged?.Invoke(false); }
    }
    public async Task PlayStreamingAsync(SpeechAudio audio, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var source = new StreamingPcm();
        var feed = source.FeedAsync(audio, linked.Token);
        using var player = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 40);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) => { if (e.Exception is not null) done.TrySetException(e.Exception); else done.TrySetResult(); };
        try
        {
            while (!source.HasData && !feed.IsCompleted) await Task.Delay(5, ct);
            if (feed.IsFaulted) await feed;
            if (!source.HasData) return;
            player.Init(source); PlaybackChanged?.Invoke(true); player.Play();
            while (!done.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                if (feed.IsFaulted) await feed;
                var feature = source.At(player.GetPosition() / (double)player.OutputWaveFormat.AverageBytesPerSecond);
                Features?.Invoke(feature.Level, feature.Bands);
                await Task.WhenAny(done.Task, Task.Delay(20, ct));
            }
            await done.Task; await feed;
        }
        finally
        {
            linked.Cancel(); player.Stop();
            LastUnderrunMs = source.UnderrunBytes * 1000 / source.WaveFormat.AverageBytesPerSecond;
            Features?.Invoke(0f, new float[AudioSpectrum.BandCount]); PlaybackChanged?.Invoke(false);
            try { await feed; } catch (OperationCanceledException) { }
        }
    }
    public void Dispose() { _local.Dispose(); }
}

/// <summary>In-memory, bounded utterance. No capture exists until the owner explicitly calls it.</summary>
public sealed class UtteranceBuffer : IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryStream _pcm = new();
    private readonly DateTime _start = DateTime.UtcNow;
    private DateTime _lastSpeech;
    private int _rate;
    public bool HeardSpeech { get; private set; }
    public void Append(byte[] pcm, int rate, float rms)
    {
        lock (_gate)
        {
            _rate = rate;
            if (_pcm.Length < rate * 2 * 30) _pcm.Write(pcm);
            if (rms > 0.008f) { HeardSpeech = true; _lastSpeech = DateTime.UtcNow; }
        }
    }
    public bool ShouldFinish
    {
        get { lock (_gate) return DateTime.UtcNow - _start > TimeSpan.FromSeconds(30) ||
            (HeardSpeech ? DateTime.UtcNow - _lastSpeech > TimeSpan.FromMilliseconds(650) : DateTime.UtcNow - _start > TimeSpan.FromSeconds(8)); }
    }
    public byte[] Wav()
    {
        lock (_gate)
        {
            using var result = new MemoryStream();
            using var writer = new BinaryWriter(result);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + (int)_pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(_rate); writer.Write(_rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((int)_pcm.Length); writer.Write(_pcm.ToArray());
            return result.ToArray();
        }
    }
    public void Dispose() { lock (_gate) _pcm.Dispose(); }
}
