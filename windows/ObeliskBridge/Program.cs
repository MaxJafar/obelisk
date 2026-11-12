using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JarvisHud.Audio;
using JarvisHud.Core;
using JarvisHud.Homelab;

Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
using var bridge = new Bridge(args.Contains("--preview"));
bridge.Start();
while (await Console.In.ReadLineAsync() is { } line)
{
    try { using var doc = JsonDocument.Parse(line); _ = Observe(bridge.Command(doc.RootElement.Clone())); }
    catch (Exception ex) { bridge.Emit("notice", new { message = ex.Message }); }
}
async Task Observe(Task command) { try { await command; } catch(Exception ex) { bridge.Emit("notice",new {message=ex.Message}); } }

sealed class Bridge : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _write = new();
    private readonly object _voiceGate = new();
    private readonly Settings _settings;
    private readonly HudController _controller;
    private readonly HomelabClient _lab;
    private readonly LabControlClient _control = new();
    private readonly SpeechClient _speech = new();
    private readonly AudioMeter _mic = new(false);
    private readonly LightDeskSync _rgb = new();
    private CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly MapOptions _options = new();
    private TopologyGraph? _graph;
    private UtteranceBuffer? _utterance;
    private RealtimeTranscription? _recognition;
    private LiveSpeechTurn? _turn;
    private string? _run;
    private string _phase = "Standby";
    private bool _listening, _busy, _visible = true;
    private int _generation;
    private Task _stopping = Task.CompletedTask;
    private int _voiceEpoch;
    private bool _acceptReply;
    private readonly HashSet<string> _stoppedRuns = [];
    private readonly bool _preview;
    private readonly Stopwatch _turnClock = new();
    private long? _sttMs;
    private long _sentAt;
    private long? _firstTextMs, _firstPlaybackMs;
    private Timer? _timer;

    public Bridge(bool preview)
    {
        _preview = preview; _settings = preview ? new Settings() : Settings.Load(); _settings.Transient = preview; _settings.Simulate = preview; _settings.MicEnabled = false;
        if(preview)_settings.AutoConnect=false;
        _controller = new(_settings); _lab = new(preview); _rgb.Enabled = !preview && _settings.LightDeskReactive;
        _controller.Changed += State;
        _lab.Changed += Map;
        _mic.Features += (level,bands) => Emit("audio", new { source="user",level,bands });
        _mic.Stopped += error => { if(error is not null) { CancelMic(); Emit("notice",new {message=error.Message}); } };
        _speech.Features += (level,bands) => { _rgb.Feed(level); if (_visible) Emit("audio",new {source="agent",level,bands}); };
        _speech.PlaybackChanged += playing => { if(playing) { _rgb.Start(); _firstPlaybackMs ??= _turnClock.ElapsedMilliseconds; } else _rgb.Stop(); _phase=playing?"Speaking":"Thinking";State(); };
        _controller.ReplyUpdated += Reply;
        _controller.ReplyCancelled += run => { if(run==_run) _turn?.Cancel(); };
    }
    public void Emit(string kind,object value)
    {
        try { var line = JsonSerializer.Serialize(new { kind, value },Json); lock(_write) Console.Out.WriteLine(line); }
        catch(IOException) { _stop.Cancel(); }
    }
    public void Start()
    {
        _controller.Start(); _lab.SetActivity(true,true);
        _timer = new Timer(_=>State(),null,1000,1000);
        Map(); State();
        if(!_preview && _settings.VoiceEnabled && LocalAudioEngine.Installed) _ = Warm();
    }
    private async Task Warm() { if(!_settings.VoiceEnabled)return; try {await _speech.PrepareAsync(_stop.Token);State();} catch(Exception ex) {if(!_stop.IsCancellationRequested)Emit("notice",new{message=ex.Message});} }
    private void State() => Emit("state",new {phase=_phase,online=_controller.IsOnline,link=_controller.Link.Summary,detail=_controller.Link.Detail,
        agent=_settings.AgentId,reply=_controller.Subtitle,listening=_listening,busy=_busy||_controller.HasOwnRun,engine=_speech.EngineStatus,
        rgb=_rgb.Enabled,rgbStatus=_rgb.Status,preview=_preview,sttMs=_sttMs,firstTextMs=_firstTextMs,firstPlaybackMs=_firstPlaybackMs});
    private void Map()
    {
        _graph=TopologyGraph.Build(_lab.Snapshot,_options);
        Emit("map",new {status=_lab.Status,refreshing=_lab.Refreshing,stale=_lab.Snapshot.IsStale,timestamp=_lab.Snapshot.Timestamp,
            devices=_lab.Snapshot.Devices.Count,online=_lab.Snapshot.Devices.Count(d=>d.Online),agents=_lab.Snapshot.Agents.Count,
            sessions=_lab.Snapshot.Sessions.Count,active=_lab.Snapshot.Sessions.Count(s=>s.Open),
            width=_graph.Width,height=_graph.Height,frames=_graph.Frames,edges=_graph.Edges,labels=_graph.Labels,
            nodes=_graph.Nodes.Select(n=>new {n.Id,kind=n.Kind.ToString(),n.Host,n.Box,n.Title,n.Meta,n.Online,n.Active,n.Match,n.Fraction})});
    }
    private void Reply(string run,string text,bool final)
    {
        if(!_settings.VoiceEnabled || !LocalAudioEngine.Installed){if(_acceptReply&&!_cts.IsCancellationRequested&&!_stoppedRuns.Contains(run)){_run=run;_firstTextMs??=_turnClock.ElapsedMilliseconds;Emit("reply",new{text,final});if(final){_busy=false;_phase="Standby";}State();}return;}
        lock(_voiceGate)
        {
            if(_cts.IsCancellationRequested || !_acceptReply || _stoppedRuns.Contains(run)) return;
            if(_run!=run)
            {
                _turn?.Cancel();_run=run;_busy=true;_phase="Thinking";_firstTextMs=_turnClock.ElapsedMilliseconds;
                _turn = new(_speech.OpenSynthesisAsync,_speech.PlayStreamingAsync,_=>{},_cts.Token);
                _ = FinishTurn(run,_turn);
            }
            try{_turn?.Update(text,final);}catch(Exception ex){Emit("notice",new{message=ex.Message});}
        }
        Emit("reply",new{text,final});State();
    }
    private async Task FinishTurn(string run,LiveSpeechTurn turn)
    {
        try{await turn.Completion;}catch(OperationCanceledException){}catch(Exception ex){Emit("notice",new{message="Voice stopped. "+ex.Message});}
        finally
        {
            if(_run==run){_turn=null;_busy=false;_phase="Standby";State();}
            var metrics=new {engine="qwen3-tts-local",sttMs=_sttMs,firstTextMs=_firstTextMs,firstPlaybackMs=_firstPlaybackMs,
                turn.FirstAudioMs,turn.SynthesizedPhrases,turn.PlayedPhrases,underrunMs=_speech.LastUnderrunMs,utc=DateTime.UtcNow};
            Directory.CreateDirectory(Settings.Directory);await File.WriteAllTextAsync(Path.Combine(Settings.Directory,"last-speech-metrics.json"),JsonSerializer.Serialize(metrics,Json));
            Emit("metrics",metrics);
        }
    }
    private void NewTurn()
    {
        _voiceEpoch++;_acceptReply=false;_cts.Dispose();_cts=new();_run=null;_sttMs=null;_firstTextMs=null;_firstPlaybackMs=null;_turnClock.Restart();
    }
    private async Task Send(string text)
    {
        if(_preview)throw new InvalidOperationException("Preview cannot send messages.");
        if(string.IsNullOrWhiteSpace(text)||text.Length>16000)throw new InvalidOperationException("Enter a message up to 16,000 characters.");
        if(_busy||_controller.HasOwnRun)throw new InvalidOperationException("Stop the current reply first.");
        NewTurn();_acceptReply=true;_busy=true;_phase="Thinking";State();_sentAt=_turnClock.ElapsedMilliseconds;
        try{await _controller.SendAsync(text);}catch{_busy=false;_phase="Standby";State();throw;}
    }
    private void Listen()
    {
        if(_listening)return;
        if(_preview||!_controller.IsOnline)throw new InvalidOperationException("Connect the Gateway before speaking.");
        if(_busy||_controller.HasOwnRun)throw new InvalidOperationException("Stop the current reply first.");
        if(!RealtimeTranscription.Available)throw new InvalidOperationException("Add your transcription API key in Settings before speaking.");
        NewTurn();var buffer=new UtteranceBuffer();_utterance=buffer;_mic.PcmData+=buffer.Append;
        if(RealtimeTranscription.Available){_recognition=new(_cts.Token);_recognition.Partial+=text=>Emit("partial",new{text});_mic.PcmData+=_recognition.Append;}
        try{_mic.Start(_settings.MicDeviceId);}catch{CancelMic();throw;}
        _listening=true;_phase="Listening";State();_ = Warm();var generation=++_generation;
        _ = Task.Run(async()=>{try{while(_listening&&generation==_generation&&!_stop.IsCancellationRequested){await Task.Delay(50,_stop.Token);if(buffer.ShouldFinish){await FinishMic();break;}}}catch(OperationCanceledException){}catch(Exception ex){CancelMic();Emit("notice",new{message=ex.Message});}});
    }
    private void CancelMic()
    {
        _generation++;_mic.Stop();_listening=false;var buffer=Interlocked.Exchange(ref _utterance,null);
        if(buffer is not null){_mic.PcmData-=buffer.Append;buffer.Dispose();}Emit("audio",new{source="user",level=0,bands=new float[24]});
        var recognition=Interlocked.Exchange(ref _recognition,null);if(recognition is not null){_mic.PcmData-=recognition.Append;recognition.Dispose();}
    }
    private async Task FinishMic()
    {
        var buffer=Interlocked.Exchange(ref _utterance,null);if(buffer is null)return;
        _generation++;_mic.Stop();_mic.PcmData-=buffer.Append;_listening=false;
        var recognition=Interlocked.Exchange(ref _recognition,null);if(recognition is not null)_mic.PcmData-=recognition.Append;
        var wav=buffer.HeardSpeech?buffer.Wav():null;buffer.Dispose();
        if(wav is null){recognition?.Dispose();_phase="Standby";State();return;}
        _phase="Transcribing";_busy=true;_turnClock.Restart();State();var watch=Stopwatch.StartNew();var epoch=_voiceEpoch;var token=_cts.Token;
        try
        {
            var text=recognition is not null?await recognition.FinishAsync(token):await _speech.TranscribeAsync(wav,token);
            token.ThrowIfCancellationRequested();if(epoch!=_voiceEpoch)throw new OperationCanceledException();_sttMs=watch.ElapsedMilliseconds;
            if(string.IsNullOrWhiteSpace(text))throw new IOException("Speech was not recognised.");
            Emit("transcript",new{text});_acceptReply=true;_phase="Thinking";State();_sentAt=_turnClock.ElapsedMilliseconds;
            await _controller.SendAsync(text);
        }
        catch(Exception ex){if(epoch==_voiceEpoch){_busy=false;_phase="Standby";if(ex is not OperationCanceledException)Emit("notice",new{message=ex.Message});State();}}
        finally{recognition?.Dispose();}
    }
    private void Details(string id,int page,int pageSize)
    {
        var node=_graph?.Nodes.FirstOrDefault(n=>n.Id==id);if(node is null)return;
        var sessions=node.Data switch {LabAgent a=>_lab.Snapshot.Sessions.Where(s=>TopologyGraph.SessionBelongs(s,a)),LabDevice d=>_lab.Snapshot.Sessions.Where(s=>s.Device==d.Id),_=>Enumerable.Empty<LabSession>()};
        pageSize=Math.Clamp(pageSize,1,6);
        var rows=sessions.OrderByDescending(s=>s.Open).ThenByDescending(s=>s.UpdatedAt).ToArray();page=Math.Clamp(page,0,Math.Max(0,(rows.Length-1)/pageSize));
        Emit("detail",new{id,node.Title,kind=node.Kind.ToString(),data=node.Kind==MapKind.Network?null:node.Data,
            endpoints=(node.Data is LabDevice device?_lab.Snapshot.Services.Where(s=>s.Device==device.Id):node.Endpoints).Select(s=>new{service=s,canControl=LabControlClient.Available(_lab.Snapshot,s,_preview)}),
            sessions=rows.Skip(page*pageSize).Take(pageSize),count=rows.Length,page,pages=Math.Max(1,(rows.Length+pageSize-1)/pageSize)});
    }
    private async Task StopVoice()
    {
        _voiceEpoch++;_acceptReply=false;_busy=true;
        if(_run is not null){if(_stoppedRuns.Count>128)_stoppedRuns.Clear();_stoppedRuns.Add(_run);}
        CancelMic();_cts.Cancel();var ending=_turn;ending?.Cancel();_phase="Stopping";State();
        try {await _controller.StopAsync();}catch(Exception ex){Emit("notice",new{message=ex.Message});}
        if(ending is not null){try{await ending.Completion;}catch{}}
        _phase="Standby";_busy=false;State();
    }
    private void Configuration() => Emit("configuration", new {
        gatewayUrl=_settings.GatewayUrl,agentId=_settings.AgentId,voiceEnabled=_settings.VoiceEnabled,
        hasTranscriptionKey=!_preview&&RealtimeTranscription.Available,localAudioInstalled=!_preview&&LocalAudioEngine.Installed,
        microphones=_preview?Array.Empty<AudioDevice>():AudioMeter.ListMicrophones(),micDeviceId=_settings.MicDeviceId,preview=_preview
    });
    private void Configure(JsonElement input)
    {
        if(_preview)throw new InvalidOperationException("The demo does not save settings.");
        if(_busy||_listening||_controller.HasOwnRun)throw new InvalidOperationException("Finish the current turn before changing settings.");
        string Read(string name)=>input.TryGetProperty(name,out var value)?value.GetString()?.Trim()??"":"";
        var raw=Read("gatewayUrl");var agent=Read("agentId");
        if(!System.Text.RegularExpressions.Regex.IsMatch(agent,"^[A-Za-z0-9_-]{1,80}$"))throw new IOException("Use a valid configured agent ID.");
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var url)||url.Scheme is not("wss" or "ws")||!string.IsNullOrEmpty(url.UserInfo)||!string.IsNullOrEmpty(url.Query)||!string.IsNullOrEmpty(url.Fragment)||url.Scheme=="ws"&&!url.IsLoopback)
            throw new IOException("Use a wss:// Gateway address, or ws:// on localhost, without credentials or query parameters.");
        var changed=_settings.GatewayUrl!=raw||_settings.AgentId!=agent;
        _controller.Unlink();
        if(changed)_settings.HudSessionKey=null;
        _settings.GatewayUrl=raw;_settings.AgentId=agent;
        _settings.MicDeviceId=Read("micDeviceId") is {Length:>0} mic?mic:null;
        _settings.VoiceEnabled=input.TryGetProperty("voiceEnabled",out var enabled)&&enabled.GetBoolean();
        var gatewayToken=Read("gatewayToken");if(gatewayToken.Length>0)SecretStore.WriteString(JarvisHud.Gateway.GatewayClient.SharedTokenSecret,gatewayToken);
        var speechKey=Read("speechKey");if(speechKey.Length>0)SecretStore.WriteString(RealtimeTranscription.Credential,speechKey);
        _settings.AutoConnect=true;_settings.Save();
        var error=_controller.LinkGateway();if(error is not null)throw new IOException(error);
        Emit("notice",new{message="Settings saved. Approve pairing on your Gateway if requested."});
        Configuration();State();
        if(_settings.VoiceEnabled&&LocalAudioEngine.Installed)_=Warm();
    }
    public async Task Command(JsonElement c)
    {
        var op=c.GetProperty("op").GetString();string Text(string name)=>c.TryGetProperty(name,out var value)?value.GetString()??"":"";
        switch(op)
        {
            case "init":State();Map();Configuration();break;
            case "configure":Configure(c);break;
            case "configuration":Configuration();break;
            case "send":await Send(Text("text"));break;
            case "mic":
                if(!_stopping.IsCompleted)break;
                if(_listening)await FinishMic();
                else {if(_busy||_controller.HasOwnRun){_stopping=StopVoice();await _stopping;}Listen();}break;
            case "stop":
                if(!_stopping.IsCompleted)break;
                _stopping=StopVoice();await _stopping;break;
            case "warm":if(!_preview)await Warm();break;
            case "refresh":_ = _lab.RefreshAsync();break;
            case "visible":_visible=c.GetProperty("value").GetBoolean();_lab.SetActivity(_visible,true);break;
            case "rgb":if(_preview)throw new IOException("Preview cannot control lighting.");_rgb.Enabled=c.GetProperty("value").GetBoolean();_settings.LightDeskReactive=_rgb.Enabled;_settings.Save();State();break;
            case "layers":
                _options.Services=c.GetProperty("services").GetBoolean();_options.Agents=c.GetProperty("agents").GetBoolean();_options.Storage=c.GetProperty("storage").GetBoolean();_options.Activity=c.GetProperty("activity").GetBoolean();_options.Checks=c.GetProperty("checks").GetBoolean();_options.Search=Text("search");_options.FocusHost=c.TryGetProperty("host",out var h)&&h.ValueKind==JsonValueKind.String?h.GetString():null;Map();break;
            case "detail":Details(Text("id"),c.TryGetProperty("page",out var p)?p.GetInt32():0,c.TryGetProperty("pageSize",out var size)?size.GetInt32():3);break;
            case "open":
                if(_preview)throw new IOException("Demo endpoints are fictional.");
                var service=_lab.Snapshot.Services.FirstOrDefault(s=>s.Id==Text("id"));
                if(service is null||!Uri.TryCreate(service.Url,UriKind.Absolute,out var url)||url.Scheme is not("http" or "https"))throw new IOException("This endpoint has no web link.");
                Process.Start(new ProcessStartInfo(url.ToString()){UseShellExecute=true});break;
            case "control":
                var target=_lab.Snapshot.Services.FirstOrDefault(s=>s.Id==Text("id"))??throw new IOException("Unknown service.");
                var result=await _control.SetRunningAsync(_lab.Snapshot,target,c.GetProperty("running").GetBoolean(),_preview);
                Emit("notice",new{message=result.Message});await _lab.RefreshAsync();break;
            default:throw new IOException("Unknown native operation.");
        }
    }
    public void Dispose(){_stop.Cancel();_timer?.Dispose();CancelMic();_cts.Cancel();_turn?.Cancel();_speech.Dispose();_rgb.Dispose();_mic.Dispose();_controller.Dispose();_control.Dispose();_lab.Dispose();}
}
