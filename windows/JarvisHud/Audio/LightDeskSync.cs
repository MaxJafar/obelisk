using System.IO.Pipes;

namespace JarvisHud.Audio;

/// <summary>Levels and color only, over a local, same-user LightDesk pipe. Never carries audio.</summary>
public sealed class LightDeskSync : IDisposable
{
    public const string PipeName = "LightDesk.JarvisAudio.v1";
    private CancellationTokenSource? _turn;
    private volatile float _level;
    private volatile bool _enabled = true;
    private Task _completion = Task.CompletedTask;
    private readonly string _pipeName;
    public Task Completion => _completion;
    public LightDeskSync(string pipeName = PipeName) => _pipeName = pipeName;
    public string Status { get; private set; } = "Ready when Jarvis speaks";
    public bool Enabled { get => _enabled; set { _enabled = value; if (!value) Stop(); } }
    public void Feed(float level) => _level = float.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
    public void Start()
    {
        Stop();
        if (!Enabled) return;
        var previous = _completion;
        var turn = new CancellationTokenSource();
        _turn = turn; _level = 0;
        _completion = RunAsync(previous, turn);
    }
    public void Stop() { var turn = Interlocked.Exchange(ref _turn, null); try { turn?.Cancel(); } catch (ObjectDisposedException) { } _level = 0; }
    private async Task RunAsync(Task previous, CancellationTokenSource turn)
    {
        try
        {
            await previous;
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var engaged = false;
            try
            {
                await pipe.ConnectAsync(350, turn.Token);
                var message = new byte[] { 76, 68, 82, 49, 1, 0, 255, 154, 82, 0, 0, 0 };
                var ack = new byte[1];
                float smooth = 0;
                while (!turn.IsCancellationRequested && Enabled)
                {
                    smooth += (_level - smooth) * (_level > smooth ? 0.7f : 0.28f);
                    message[5] = (byte)Math.Clamp((int)(smooth * 255), 0, 255);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);
                    deadline.CancelAfter(650);
                    await pipe.WriteAsync(message, deadline.Token);
                    await pipe.ReadExactlyAsync(ack, deadline.Token);
                    Status = ack[0] == 1 ? "Synced to Jarvis voice" : ack[0] == 2 ? "LightDesk manual control" : "LightDesk: no saved device lighting";
                    engaged |= ack[0] == 1;
                    if (ack[0] == 0) break;
                    await Task.Delay(50, turn.Token);
                }
            }
            finally
            {
                if (pipe.IsConnected)
                {
                    try
                    {
                        using var end = new CancellationTokenSource(600);
                        await pipe.WriteAsync(new byte[] {76,68,82,49,0,0,0,0,0,0,0,0}, end.Token);
                        var restored = new byte[1];
                        await pipe.ReadExactlyAsync(restored, end.Token);
                        if (engaged) Status = restored[0] == 1 ? "Lighting restored" : "Check LightDesk restoration status";
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { Status = "LightDesk disconnected; restore watchdog active"; }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { Status = "Open LightDesk to sync RGB"; }
        finally { Interlocked.CompareExchange(ref _turn, null, turn); turn.Dispose(); }
    }
    public void Dispose() => Stop();
}
