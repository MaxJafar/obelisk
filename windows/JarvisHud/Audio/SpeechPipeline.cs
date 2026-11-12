using System.Diagnostics;
using System.Threading.Channels;

namespace JarvisHud.Audio;

/// <summary>Turns cumulative, user-visible reply snapshots into ordered phrases without a total-text cutoff.</summary>
public sealed class SpeechPhrases
{
    private string _accepted = "";
    private int _offset;
    private bool _first = true;
    private bool _complete;
    public int AcceptedCharacters => _accepted.Length;

    public IReadOnlyList<string> Update(string snapshot, bool final)
    {
        if (_complete) return Array.Empty<string>();
        // A snapshot may revise unspoken text, but cannot retract already submitted speech.
        if (snapshot.Length < _offset || !snapshot.AsSpan(0, _offset).SequenceEqual(_accepted.AsSpan(0, _offset)))
            throw new IOException("The streamed reply revised text already sent to speech. Full corrected text remains in the chat.");
        _accepted = snapshot;
        var result = new List<string>();
        while (_offset < snapshot.Length)
        {
            var remaining = snapshot[_offset..];
            var target = _first ? 80 : 200;
            var minimum = _first ? 16 : 90;
            var end = 0;
            for (var i = 0; i < Math.Min(remaining.Length, target); i++)
            {
                if (i + 1 >= minimum && ".!?;:\n".Contains(remaining[i]) &&
                    (i + 1 < remaining.Length ? char.IsWhiteSpace(remaining[i + 1]) : final))
                { end = i + 1; break; }
            }
            if (end == 0 && remaining.Length > target)
            {
                end = remaining.LastIndexOf(' ', target - 1, target);
                if (end < 1) end = target;
                // Never split a UTF-16 surrogate pair.
                if (char.IsHighSurrogate(remaining[end - 1])) end--;
            }
            if (end == 0 && final) end = remaining.Length;
            if (end == 0) break;
            var phrase = remaining[..end].Trim();
            _offset += end;
            if (phrase.Length == 0) continue;
            result.Add(phrase); _first = false;
        }
        _complete = final;
        return result;
    }
}

/// <summary>Streaming PCM playback with one bounded phrase of lookahead.</summary>
public sealed class LiveSpeechTurn
{
    private readonly SpeechPhrases _phrases = new();
    private readonly Channel<string> _text = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<SpeechAudio> _audio = Channel.CreateBounded<SpeechAudio>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts;
    private readonly Stopwatch _time = Stopwatch.StartNew();
    public Task Completion { get; }
    public int AcceptedCharacters => _phrases.AcceptedCharacters;
    public int SynthesizedPhrases { get; private set; }
    public int PlayedPhrases { get; private set; }
    public long? FirstAudioMs { get; private set; }
    public long? FirstPlaybackMs { get; private set; }
    public long? FinalTextMs { get; private set; }

    public LiveSpeechTurn(Func<string, CancellationToken, Task<SpeechAudio>> synthesize,
        Func<SpeechAudio, CancellationToken, Task> play, Action<bool> playback, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Completion = RunAsync(synthesize, play, playback);
    }
    public void Update(string snapshot, bool final)
    {
        if (_cts.IsCancellationRequested || Completion.IsCompleted) return;
        try
        {
            foreach (var phrase in _phrases.Update(snapshot, final))
                if (!_text.Writer.TryWrite(phrase)) throw new IOException("Speech queue closed unexpectedly.");
            if (final) { FinalTextMs = _time.ElapsedMilliseconds; _text.Writer.TryComplete(); }
        }
        catch { Cancel(); throw; }
    }
    public void Cancel() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } _text.Writer.TryComplete(); }

    private async Task RunAsync(Func<string, CancellationToken, Task<SpeechAudio>> synthesize,
        Func<SpeechAudio, CancellationToken, Task> play, Action<bool> playback)
    {
        var producer = ProduceAsync(synthesize);
        try
        {
            await foreach (var bytes in _audio.Reader.ReadAllAsync(_cts.Token))
            {
                _cts.Token.ThrowIfCancellationRequested();
                FirstPlaybackMs ??= _time.ElapsedMilliseconds;
                playback(true);
                try { await play(bytes, _cts.Token); PlayedPhrases++; }
                finally { playback(false); }
            }
            await producer;
        }
        finally
        {
            _cts.Cancel();
            try { await producer; } catch { /* Preserve the original consumer/producer failure. */ }
            _cts.Dispose();
        }
    }
    private async Task ProduceAsync(Func<string, CancellationToken, Task<SpeechAudio>> synthesize)
    {
        try
        {
            await foreach (var phrase in _text.Reader.ReadAllAsync(_cts.Token))
            {
                // Wait before inference, not after: at most one completed future WAV is resident.
                if (!await _audio.Writer.WaitToWriteAsync(_cts.Token)) break;
                var bytes = await synthesize(phrase, _cts.Token);
                SynthesizedPhrases++; FirstAudioMs ??= _time.ElapsedMilliseconds;
                await _audio.Writer.WriteAsync(bytes, _cts.Token);
                // The native engine is single-flight; wait for this HTTP stream before admitting another.
                await bytes.Completion;
            }
            _audio.Writer.TryComplete();
        }
        catch (Exception ex) { _audio.Writer.TryComplete(ex); throw; }
    }
}
