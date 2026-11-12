using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JarvisHud.Core;
using NAudio.Dsp;

namespace JarvisHud.Audio;

/// <summary>One explicitly opened microphone turn; sends live PCM directly from this PC.</summary>
public sealed class RealtimeTranscription : IDisposable
{
    public const string Credential = "speech-api-key";
    private readonly ClientWebSocket _socket = new();
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(64);
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WdlResampler _resampler = new();
    private readonly object _gate = new();
    private readonly StringBuilder _partial = new();
    private readonly Task _run;
    private int _rate;
    private int _inputSamples;
    private string? _committed;
    private bool _finished;
    public event Action<string>? Partial;
    public static bool Available => SecretStore.Read(Credential) is { Length: > 20 };

    public RealtimeTranscription(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);_cts.CancelAfter(TimeSpan.FromSeconds(55));
        _resampler.SetMode(true, 2, false);_resampler.SetFeedMode(true);
        var key = SecretStore.ReadString(Credential) ?? throw new IOException("Realtime recognition is not configured on this PC.");
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _run = RunAsync();
    }
    public void Append(byte[] pcm, int rate, float rms)
    {
        lock (_gate)
        {
            if (_finished || _cts.IsCancellationRequested) return;
            if (rate is < 8000 or > 192000 || pcm.Length % 2 != 0) { Fail(new IOException("Unsupported microphone format.")); return; }
            if (_rate == 0) { _rate = rate; _resampler.SetRates(rate, 24000); }
            if (_rate != rate) { Fail(new IOException("Microphone format changed mid-turn.")); return; }
            _inputSamples += pcm.Length/2;
            if (_inputSamples > rate * 31) { Fail(new IOException("Voice turn exceeded 30 seconds.")); return; }
            var count = pcm.Length / 2;
            _resampler.ResamplePrepare(count, 1, out var input);
            for (var i=0; i<count; i++) input[i] = BitConverter.ToInt16(pcm, i*2)/32768f;
            var output = new float[(int)Math.Ceiling(count*24000d/rate)+128];
            var n = _resampler.ResampleOut(output, count, output.Length, 1);
            var bytes = new byte[n*2];
            for (var i=0; i<n; i++) { var sample=(short)Math.Clamp((int)(output[i]*32767),-32768,32767);bytes[i*2]=(byte)sample;bytes[i*2+1]=(byte)(sample>>8); }
            if(n>0&&!_audio.Writer.TryWrite(bytes))Fail(new IOException("Recognition connection could not keep up. Audio was not dropped or retried."));
        }
    }
    public async Task<string> FinishAsync(CancellationToken ct)
    {
        lock (_gate) { _finished=true;_audio.Writer.TryComplete(); }
        var result = await _final.Task.WaitAsync(TimeSpan.FromSeconds(15),ct);
        await _run;
        return result.Trim();
    }
    private void Fail(Exception ex) { _ready.TrySetException(ex); _final.TrySetException(ex); _audio.Writer.TryComplete(ex); _cts.Cancel(); }
    private async Task Send(object value) => await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value),WebSocketMessageType.Text,true,_cts.Token);
    private async Task RunAsync()
    {
        Task? receive=null;
        try
        {
            await _socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?intent=transcription"),_cts.Token);
            receive=ReceiveAsync();
            await Send(new {type="session.update",session=new {type="transcription",audio=new {input=new {
                format=new {type="audio/pcm",rate=24000},transcription=new {model="gpt-live-transcribe",languages=new[]{"ru","en"},delay="low",
                    prompt="Russian and English technical conversation. Preserve product names and numbers.",keywords=new[]{"Obelisk","Jarvis","Джарвис","Docker","Tailscale","SSH","OpenClaw","Qwen"}},turn_detection=(object?)null}}}});
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(12),_cts.Token);
            await foreach (var pcm in _audio.Reader.ReadAllAsync(_cts.Token)) await Send(new {type="input_audio_buffer.append",audio=Convert.ToBase64String(pcm)});
            await Send(new {type="input_audio_buffer.commit"});
            await _final.Task.WaitAsync(TimeSpan.FromSeconds(15),_cts.Token);
        }
        catch(Exception ex) { _ready.TrySetException(ex);_final.TrySetException(ex); }
        finally
        {
            _cts.Cancel();_socket.Abort();
            if(receive is not null)try{await receive;}catch{}
        }
    }
    private async Task ReceiveAsync()
    {
        var buffer=new byte[16384];using var message=new MemoryStream();
        try
        {
            while(!_cts.IsCancellationRequested)
            {
                message.SetLength(0);WebSocketReceiveResult result;
                do {result=await _socket.ReceiveAsync(buffer,_cts.Token);if(result.MessageType==WebSocketMessageType.Close)throw new IOException("Recognition connection closed before completion.");message.Write(buffer,0,result.Count);if(message.Length>1024*1024)throw new IOException("Recognition response is too large.");}while(!result.EndOfMessage);
                using var doc=JsonDocument.Parse(message.ToArray());var e=doc.RootElement;var type=e.GetProperty("type").GetString();
                if(type=="session.updated")_ready.TrySetResult();
                else if(type=="input_audio_buffer.committed")_committed=e.GetProperty("item_id").GetString();
                else if(type=="conversation.item.input_audio_transcription.delta") { _partial.Append(e.GetProperty("delta").GetString());Partial?.Invoke(_partial.ToString()); }
                else if(type=="conversation.item.input_audio_transcription.completed" && _committed==e.GetProperty("item_id").GetString()) {_final.TrySetResult(e.GetProperty("transcript").GetString()??"");return;}
                else if(type is "error" or "conversation.item.input_audio_transcription.failed")throw new IOException("Realtime recognition rejected this turn. No automatic retry.");
            }
        }
        catch(Exception ex){Fail(ex);}
    }
    public void Dispose(){_cts.Cancel();_socket.Abort();_audio.Writer.TryComplete();_ = _run.ContinueWith(_=>{_socket.Dispose();_cts.Dispose();},TaskScheduler.Default);}
}
