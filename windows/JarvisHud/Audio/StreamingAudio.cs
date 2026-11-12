using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Wave;

namespace JarvisHud.Audio;

/// <summary>Bounded PCM transport; the first block is playable before inference finishes.</summary>
public sealed class SpeechAudio
{
    private readonly Channel<byte[]> _pcm = Channel.CreateBounded<byte[]>(8);
    public ChannelReader<byte[]> Chunks => _pcm.Reader;
    public Task Completion { get; }
    public SpeechAudio(HttpResponseMessage response, Stream stream, CancellationToken ct) => Completion = PumpAsync(response, stream, ct);
    private async Task PumpAsync(HttpResponseMessage response, Stream stream, CancellationToken ct)
    {
        try
        {
            using (response)
            await using (stream)
            {
                var buffer = new byte[8192]; var carry = 0;
                while (true)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct);
                    if (n == 0) { if (carry != 0) throw new IOException("Truncated PCM frame."); break; }
                    n += carry; var aligned = n / 4 * 4;
                    if (aligned > 0) await _pcm.Writer.WriteAsync(buffer.AsSpan(0, aligned).ToArray(), ct);
                    carry = n - aligned;
                    if (carry > 0) buffer.AsSpan(aligned, carry).CopyTo(buffer);
                }
            }
            _pcm.Writer.TryComplete();
        }
        catch (Exception ex) { _pcm.Writer.TryComplete(ex); throw; }
    }
}

/// <summary>WASAPI source with a bounded queue and metering indexed by real output position.</summary>
internal sealed class StreamingPcm : IWaveProvider
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _blocks = new();
    private readonly Queue<(long Start, AudioFeatures Features)> _features = new();
    private readonly AudioSpectrum _spectrum = new();
    private int _offset, _buffered;
    private long _position;
    private bool _complete;
    private AudioFeatures _last = new(0, new float[AudioSpectrum.BandCount]);
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(24000, 1);
    public long UnderrunBytes { get; private set; }
    public bool HasData { get { lock (_gate) return _buffered > 0; } }
    public async Task FeedAsync(SpeechAudio audio, CancellationToken ct)
    {
        try
        {
            await foreach (var block in audio.Chunks.ReadAllAsync(ct))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (_buffered < 24000 * 4 * 3) { _blocks.Enqueue(block); _buffered += block.Length; break; }
                    }
                    await Task.Delay(10, ct);
                }
            }
            await audio.Completion;
        }
        finally { lock (_gate) _complete = true; }
    }
    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            var count = buffer.Length;
            if (_complete && _buffered == 0) return 0;
            var wrote = 0;
            while (wrote < count && _blocks.TryPeek(out var block))
            {
                var n = Math.Min(count - wrote, block.Length - _offset);
                block.AsSpan(_offset, n).CopyTo(buffer.Slice(wrote, n));
                wrote += n; _offset += n; _buffered -= n;
                if (_offset == block.Length) { _blocks.Dequeue(); _offset = 0; }
            }
            var size = _complete ? wrote : count;
            if (wrote < size) { buffer.Slice(wrote, size - wrote).Clear(); UnderrunBytes += size - wrote; }
            var samples = MemoryMarshal.Cast<byte, float>(buffer[..size]);
            for (var at = 0; at < samples.Length; at += 480)
                _features.Enqueue((_position + at * 4, _spectrum.Process(samples.Slice(at, Math.Min(480, samples.Length - at)), 24000)));
            _position += size;
            return size;
        }
    }
    public AudioFeatures At(double seconds)
    {
        lock (_gate)
        {
            var position = (long)(seconds * WaveFormat.AverageBytesPerSecond);
            while (_features.TryPeek(out var frame) && frame.Start <= position) { _last = _features.Dequeue().Features; }
            return _last;
        }
    }
}
