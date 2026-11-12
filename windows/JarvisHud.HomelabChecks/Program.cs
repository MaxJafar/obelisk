using JarvisHud.Homelab;
using System.Text.Json;

static void Check(bool ok, string message) { if(!ok) throw new Exception(message); Console.WriteLine("PASS "+message); }
var previous = new LabSnapshot { SchemaVersion=1, Timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    Agents=[new() { Id="mac:jarvis",Device="mac",Name="Jarvis" }],
    Sessions=[new() { Id="mac:session",Device="mac",State="Running",TotalTokens=123,UsageKind="Latest context / last turn" }] };
var outage=new LabSnapshot { SchemaVersion=1, Devices=[new() { Id="mac",Online=false,TelemetryStatus="Offline" }] };
outage.KeepUnavailableInventory(previous);
Check(outage.Sessions.Count==1 && !outage.Sessions[0].Open && outage.Agents[0].TelemetryStale,"Offline inventory survives without advertising live activity");
var back=new LabSnapshot { SchemaVersion=1,Devices=[new() { Id="mac",Online=true,TelemetryStatus="Connected" }],Sessions=[new() { Id="mac:session",Device="mac",State="Idle",TotalTokens=456 }] };
back.KeepUnavailableInventory(outage);
Check(back.Sessions.Count==1 && !back.Sessions[0].TelemetryStale && back.Sessions[0].TotalTokens==456,"Reconnected telemetry replaces cached usage without duplicates");
var serialized=JsonSerializer.Serialize(outage,LabSnapshot.Json);
Check(LabSnapshot.Parse(serialized).Sessions[0] is { TelemetryStale:true, InputTokens:null, TotalTokens:123 },"Cache preserves stale state and distinguishes missing usage from zero");
try { LabSnapshot.Parse("{\"schemaVersion\":999}");throw new Exception("Unknown schema accepted"); }
catch(JsonException) { Console.WriteLine("PASS Unsupported snapshot schema is rejected"); }
Check(new LabSnapshot { Timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-121 }.IsStale,"Expired snapshots become stale");
var lab=new LabSnapshot {SchemaVersion=1,Timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    Devices=[new(){Id="pc",Name="Workstation",Online=true},new(){Id="server",Name="Server",Online=true},new(){Id="mac",Name="Agent host",Online=true}],
    Agents=[new(){Id="pc:codex:Codex",Name="Codex",Device="pc",Provider="Codex",Model="gpt-test"},new(){Id="mac:codex:Codex",Name="Codex",Device="mac",Provider="Codex"}],
    Services=[new(){Id="main",Name="Service",Device="server",Container="app",Status="Healthy"},new(){Id="alternate",Name="Alternate route",Device="server",Container="app",Status="Healthy"},new(){Id="external",Name="External",Device="external",Url="https://example.test",Status="Link only"}],
    Sessions=Enumerable.Range(0,8).Select(i=>new LabSession {Id="session-"+i,Agent="Codex",Device="pc",Provider="Codex",Title="Current project "+i,State="Open turn",UpdatedAt=10+i}).ToList()};
var options=new MapOptions();
var graph=TopologyGraph.Build(lab,options);
Check(graph.Nodes.Count(n=>n.Kind==MapKind.Session)==2&&graph.Nodes.Single(n=>n.Kind==MapKind.SessionGroup).Host=="pc","Active sessions are bounded with a navigator for overflow");
Check(graph.Nodes.Where(n=>n.Kind==MapKind.Session).All(n=>n.Host=="pc"),"Same-named agents on separate hosts cannot borrow each other's sessions");
Check(graph.Nodes.Single(n=>n.Id=="service:main").Endpoints.Count==2,"Multiple routes to one observed container share a control and retain both endpoints");
Check(graph.Edges.All(e=>graph.Nodes.Any(n=>n.Id==e.From)&&graph.Nodes.Any(n=>n.Id==e.To))&&
    !graph.Edges.Any(e=>e.To=="service:external"),"Every wire has observed endpoints and external bookmarks gain no fictitious host");
Check(graph.Nodes.All(n=>n.Box.X>=0&&n.Box.Y>=0&&n.Box.Right<=graph.Width&&n.Box.Bottom<=graph.Height),"Fit bounds include every node");
options.Collapsed.Add("pc");
var collapsed=TopologyGraph.Build(lab,options);
Check(collapsed.Nodes.Any(n=>n.Id=="device:pc")&&!collapsed.Nodes.Any(n=>n.Host=="pc"&&n.Kind!=MapKind.Device),"Collapsing a host removes its branches but retains the host");
options.Collapsed.Clear();options.FocusHost="mac";
var focused=TopologyGraph.Build(lab,options);
Check(focused.Nodes.All(n=>n.Kind==MapKind.Network||n.Host=="mac"),"Isolating a host excludes unrelated inventory");
options.FocusHost=null;options.Search="project 7";
var searched=TopologyGraph.Build(lab,options);
Check(searched.Nodes.Any(n=>n.Kind==MapKind.Session&&n.Match&&n.Id=="session:session-7"),"Search reveals matching sessions outside the visible active subset");
lab.Timestamp=1;
Check(!TopologyGraph.Build(lab,new()).Edges.Any(e=>e.Online),"Expired snapshots stop advertising live links");
Check(TopologyGraph.UptimePercent(1)=="100.0%"&&TopologyGraph.UptimePercent(0.997)=="99.7%"&&TopologyGraph.UptimePercent(null)=="Not reported","Kuma fractions display as percentages without turning missing history into zero");
Console.WriteLine("15 homelab model and topology checks passed.");
if(args.Length>0)
{
    var real=LabSnapshot.Parse(File.ReadAllText(args[0]));
    var configurations=new List<MapOptions>{new(),new(){Storage=true,Checks=true},new(){Agents=false},new(){Services=false},new(){Search="claude"}};
    configurations.AddRange(real.Devices.Select(d=>new MapOptions{FocusHost=d.Id,Storage=true,Checks=true}));
    foreach(var config in configurations)
    {
        var map=TopologyGraph.Build(real,config);
        if(map.Nodes.Select(n=>n.Id).Distinct().Count()!=map.Nodes.Count)throw new Exception("Duplicate graph IDs");
        if(map.Nodes.Any(n=>n.Box.X<0||n.Box.Y<0||n.Box.Right>map.Width+.01||n.Box.Bottom>map.Height+.01))throw new Exception("Live inventory exceeds fit bounds");
        if(map.Edges.Any(e=>!map.Nodes.Any(n=>n.Id==e.From)||!map.Nodes.Any(n=>n.Id==e.To)))throw new Exception("Live inventory has a dangling edge");
        if(map.Edges.Any(e=>e.From!="network"&&map.Nodes.First(n=>n.Id==e.From).Host!=map.Nodes.First(n=>n.Id==e.To).Host))throw new Exception("Ownership wire crosses hosts");
    }
    Check(true,"Fixture inventory has bounded, unique nodes and correct ownership in every layer and isolated host view");
    Console.WriteLine("16 homelab checks passed, including fixture inventory.");
}
