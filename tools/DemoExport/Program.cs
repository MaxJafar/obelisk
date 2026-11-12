using System.Text.Json;
using JarvisHud.Homelab;
var root=Path.GetFullPath(args[0]);
var lab=LabSnapshot.Parse(File.ReadAllText(Path.Combine(root,"examples/demo-snapshot.json")));
if(lab.Devices.Any(d=>d.Source!="Synthetic demo"))throw new Exception("Only synthetic data may be exported.");
lab.Timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var maps=new Dictionary<string,object>();var details=new Dictionary<string,object>();
foreach(var host in new string?[]{null}.Concat(lab.Devices.Select(d=>(string?)d.Id)))
for(var mask=0;mask<32;mask++){
 var graph=TopologyGraph.Build(lab,new(){Services=(mask&1)!=0,Agents=(mask&2)!=0,Storage=(mask&4)!=0,Activity=(mask&8)!=0,Checks=(mask&16)!=0,FocusHost=host});
 maps[$"{mask}:{host??"all"}"]=new{status="Synthetic demo / no devices connected",stale=false,timestamp=1900000000,devices=lab.Devices.Count,online=lab.Devices.Count,agents=lab.Agents.Count,sessions=lab.Sessions.Count,width=graph.Width,height=graph.Height,frames=graph.Frames,edges=graph.Edges,labels=graph.Labels,nodes=graph.Nodes.Select(n=>new{n.Id,kind=n.Kind.ToString(),n.Host,n.Box,n.Title,n.Meta,n.Online,n.Active,n.Match,n.Fraction})};
 foreach(var n in graph.Nodes){
  var sessions=n.Data switch{LabAgent a=>lab.Sessions.Where(s=>TopologyGraph.SessionBelongs(s,a)),LabDevice d=>lab.Sessions.Where(s=>s.Device==d.Id),_=>Enumerable.Empty<LabSession>()};
  var endpoints=n.Data is LabDevice device?lab.Services.Where(s=>s.Device==device.Id):n.Endpoints;
  details[n.Id]=new{n.Id,n.Title,kind=n.Kind.ToString(),data=n.Kind==MapKind.Network?null:n.Data,endpoints=endpoints.Select(s=>new{service=s,canControl=false}),sessions=sessions.OrderByDescending(s=>s.Open).ThenByDescending(s=>s.UpdatedAt)};
 }
}
var json=JsonSerializer.Serialize(new{maps,details},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
File.WriteAllText(Path.Combine(root,"tauri/web/demo-data.js"),"// Generated from examples/demo-snapshot.json. Synthetic data only.\nwindow.ObeliskFixture="+json+";\n");
Console.WriteLine($"Exported {maps.Count} synthetic map views.");
