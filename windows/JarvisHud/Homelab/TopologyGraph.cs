namespace JarvisHud.Homelab;

public enum MapKind { Network, Device, Service, Agent, Session, SessionGroup, Volume, Monitor }
public readonly record struct MapPoint(float X, float Y);
public readonly record struct MapBox(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public MapPoint Top => new(X + Width / 2, Y);
    public MapPoint Left => new(X, Y + Height / 2);
    public MapPoint Lower => new(X + Width / 2, Bottom);
}
public sealed record MapNode(string Id, MapKind Kind, string Host, MapBox Box, string Title, string Meta, object Data)
{
    public bool Online { get; init; } = true;
    public bool Active { get; init; }
    public bool Match { get; init; } = true;
    public float Fraction { get; init; } = -1;
    public IReadOnlyList<LabService> Endpoints { get; init; } = [];
}
public sealed record MapEdge(string From, string To, MapPoint[] Points, bool Online, bool Active = false);
public sealed record MapFrame(string Host, string Title, MapBox Box, bool Online);
public sealed record MapLabel(string Text, float X, float Y);
public sealed class MapOptions
{
    public bool Services { get; set; } = true;
    public bool Agents { get; set; } = true;
    public bool Storage { get; set; }
    public bool Activity { get; set; } = true;
    public bool Checks { get; set; }
    public string Search { get; set; } = "";
    public string? FocusHost { get; set; }
    public HashSet<string> Collapsed { get; } = [];
}

/// <summary>Observed ownership and logical Tailscale membership, never inferred physical wiring.</summary>
public sealed class TopologyGraph
{
    public List<MapNode> Nodes { get; } = [];
    public List<MapEdge> Edges { get; } = [];
    public List<MapFrame> Frames { get; } = [];
    public List<MapLabel> Labels { get; } = [];
    public float Width { get; private set; }
    public float Height { get; private set; }
    public int Matches => Nodes.Count(n => n.Match && n.Kind != MapKind.Network);
    public static bool SessionBelongs(LabSession s, LabAgent a) => s.Device == a.Device && s.Provider == a.Provider && (s.Agent == a.Id || s.Agent == a.Name);
    public static bool Contains(string query, params string?[] text) => string.IsNullOrWhiteSpace(query) ||
        text.Any(t => t?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    public static TopologyGraph Build(LabSnapshot snapshot, MapOptions options)
    {
        var g = new TopologyGraph();
        var query = options.Search.Trim();
        bool Match(params string?[] values) => Contains(query, values);
        var hosts = snapshot.Devices.Where(d => options.FocusHost is null || options.FocusHost == d.Id)
            .OrderBy(d => d.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        var widths = hosts.Select(d => options.FocusHost is not null ? 920f :
            snapshot.Services.Count(s=>s.Device==d.Id)>4 ? 450f :
            snapshot.Agents.Count(a=>a.Device==d.Id)>2 ? 360f : 260f).ToArray();
        g.Width = Math.Max(options.FocusHost is null?760:360, widths.Sum() + Math.Max(0,hosts.Length - 1) * 24 + 32);
        var hub = new MapNode("network",MapKind.Network,"",new(g.Width/2-108,16,216,56),"Home lab","LOGICAL CONNECTIONS",snapshot)
        { Online=!snapshot.IsStale&&snapshot.Devices.Any(d=>d.Online) };
        if(options.FocusHost is null)
        {
            g.Nodes.Add(hub);
            g.Labels.Add(new("HOST OWNERSHIP  /  VERIFIED ENDPOINTS",16,118));
        }
        var peers = snapshot.Devices.Where(d => !hosts.Contains(d) && options.FocusHost is null).ToArray();
        for (var i=0; i<peers.Length; i++)
        {
            var x=i<2 ? 16+i*178 : g.Width-188-(i-2)*178;
            var n=new MapNode("device:"+peers[i].Id,MapKind.Device,peers[i].Id,new(x,24,166,48),ShortHost(peers[i]),peers[i].Online?"Connected peer":"Offline peer",peers[i])
            { Online=peers[i].Online&&!snapshot.IsStale,Match=Match(peers[i].Name,peers[i].Ip,peers[i].Dns,peers[i].Role) };
            g.Nodes.Add(n);
            g.Edges.Add(new(hub.Id,n.Id,[hub.Box.Lower,new(hub.Box.Lower.X,94),new(n.Box.Lower.X,94),n.Box.Lower],n.Online));
        }
        float left=(g.Width-widths.Sum()-Math.Max(0,hosts.Length-1)*24)/2, bottom=300;
        for(var hi=0;hi<hosts.Length;hi++)
        {
            var d=hosts[hi]; var w=widths[hi]; var hostId="device:"+d.Id;
            var live=d.Online&&!snapshot.IsStale;
            var top=options.FocusHost is null?138:16;
            var host=new MapNode(hostId,MapKind.Device,d.Id,new(left,top,w,84),ShortHost(d),d.Ip,d)
            {Online=live,Match=Match(d.Name,d.Ip,d.Dns,d.Role)};
            g.Nodes.Add(host);
            if(options.FocusHost is null)g.Edges.Add(new(hub.Id,host.Id,[hub.Box.Lower,new(hub.Box.Lower.X,108),new(host.Box.Top.X,108),host.Box.Top],live));
            float y=top+100;
            void Section(string title) { g.Labels.Add(new(title,left+18,y)); y+=22; }
            void Branch(MapNode node, string? parent=null, bool active=false)
            {
                g.Nodes.Add(node);
                var from=g.Nodes.First(n=>n.Id==(parent??hostId));
                var px=parent is null ? left+8 : from.Box.X+8;
                g.Edges.Add(new(from.Id,node.Id,[new(px,from.Box.Bottom),new(px,node.Box.Left.Y),node.Box.Left],live&&node.Online,active));
            }
            if(!options.Collapsed.Contains(d.Id))
            {
                if(options.Services || query.Length>0)
                {
                    var groups=snapshot.Services.Where(s=>s.Device==d.Id).GroupBy(s=>s.Container is {Length:>0}?s.Container:s.Id).ToArray();
                    if(groups.Length>0) Section("SERVICES / "+groups.Length.ToString("00"));
                    var cols=options.FocusHost is not null?4:w>=420?3:2;
                    var cell=(w-36-(cols-1)*12)/cols;
                    for(var i=0;i<groups.Length;i++)
                    {
                        var endpoints=groups[i].ToArray(); var s=endpoints[0];
                        var n=new MapNode("service:"+s.Id,MapKind.Service,d.Id,new(left+18+(i%cols)*(cell+12),y+(i/cols)*52,cell,44),ShortService(s),s.ContainerRunning is { } running ? running?"Running":"Stopped":ServiceState(s),s)
                        {Online=live&&s.Status is "Healthy" or "Auth required" or "Reachable",Endpoints=endpoints,Match=endpoints.Any(e=>Match(e.Name,e.Group,e.Url,e.Container))};
                        Branch(n);
                    }
                    if(groups.Length>0) y+=(float)Math.Ceiling(groups.Length/(double)cols)*52+8;
                }
                if(options.Agents || query.Length>0)
                {
                    var agents=snapshot.Agents.Where(a=>a.Device==d.Id).ToArray();
                    if(agents.Length>0) Section("AGENTS / "+agents.Length.ToString("00"));
                    var agentColumns=w>=340&&agents.Length>2?2:1;
                    var agentWidth=(w-36-(agentColumns-1)*12)/agentColumns;
                    var columnY=Enumerable.Repeat(y,agentColumns).ToArray();
                    for(var ai=0;ai<agents.Length;ai++)
                    {
                        var a=agents[ai];var column=ai%agentColumns;var ax=left+18+column*(agentWidth+12);y=columnY[column];
                        var sessions=snapshot.Sessions.Where(s=>SessionBelongs(s,a)).OrderByDescending(s=>s.UpdatedAt).ToArray();
                        var active=live&&!a.TelemetryStale&&sessions.Any(s=>s.Open);
                        var n=new MapNode("agent:"+a.Id,MapKind.Agent,d.Id,new(ax,y,agentWidth,52),a.Name,ModelName(a.Model),a)
                        { Online=live&&!a.TelemetryStale,Active=active,Match=Match(a.Name,a.Provider,a.Model) || sessions.Any(s=>Match(s.Title,s.Id,s.Model,s.Workspace)) };
                        var runtime=g.Nodes.FirstOrDefault(r=>r.Kind==MapKind.Service&&r.Host==d.Id&&r.Data is LabService rs&&rs.Name.Equals(a.Provider,StringComparison.OrdinalIgnoreCase));
                        Branch(n,runtime?.Id,active); y+=60;
                        var visible=sessions.Where(s=>query.Length>0 ? Match(s.Title,s.Id,s.Model,s.Workspace) : options.Activity&&s.Open).ToArray();
                        foreach(var s in visible.Take(2))
                        {
                            Branch(new("session:"+s.Id,MapKind.Session,d.Id,new(ax+12,y,agentWidth-12,43),s.Title,s.State+" · "+Count(s.TotalTokens)+" tok",s)
                            {Online=live&&!s.TelemetryStale,Active=live&&s.Open,Match=Match(s.Title,s.Id,s.Model,s.Workspace)},n.Id,live&&s.Open);
                            y+=51;
                        }
                        if(visible.Length>2)
                        {
                            Branch(new("sessions:"+a.Id,MapKind.SessionGroup,d.Id,new(ax+12,y,agentWidth-12,36),"+ "+(visible.Length-2)+" more sessions","Open session navigator",a){Match=true,Online=live},n.Id);
                            y+=44;
                        }
                        columnY[column]=y;
                    }
                    y=columnY.Max();
                }
                if(options.Storage)
                {
                    if(d.Disks.Count>0) Section("VOLUMES / "+d.Disks.Count.ToString("00"));
                    var volumeCols=options.FocusHost is not null?3:1;
                    var volumeWidth=(w-36-(volumeCols-1)*12)/volumeCols;
                    for(var di=0;di<d.Disks.Count;di++)
                    {
                        var disk=d.Disks[di];
                        Branch(new("volume:"+d.Id+":"+disk.Id,MapKind.Volume,d.Id,new(left+18+(di%volumeCols)*(volumeWidth+12),y+(di/volumeCols)*56,volumeWidth,48),disk.Mount,Bytes(disk.FreeBytes)+" free / "+Bytes(disk.TotalBytes),disk)
                        {Online=live,Match=Match(disk.Name,disk.Mount,disk.Filesystem),Fraction=disk.TotalBytes>0?(float)(disk.UsedBytes/disk.TotalBytes):0});
                    }
                    y+=(float)Math.Ceiling(d.Disks.Count/(double)volumeCols)*56;
                }
            }
            else { g.Labels.Add(new("BRANCH COLLAPSED",left+18,y)); y+=24; }
            var frameBottom=Math.Max(top+142,y+8);
            g.Frames.Add(new(d.Id,d.Name,new(left,top,w,frameBottom-top),live));
            bottom=Math.Max(bottom,frameBottom); left+=w+24;
        }
        // Unassigned bookmarks and uptime checks are separate observations, not invented host links.
        var links=snapshot.Services.Where(s=>string.IsNullOrEmpty(s.Device)||!snapshot.Devices.Any(d=>d.Id==s.Device)).ToArray();
        if(options.Services && options.FocusHost is null && links.Length>0)
        {
            var first=g.Frames.OrderBy(f=>f.Box.Bottom).FirstOrDefault();
            var lx=first?.Box.X??16;
            float y=(first?.Box.Bottom??bottom)+34; g.Labels.Add(new("EXTERNAL / NOT PROBED",lx,y)); y+=24;
            float linkWidth=first?.Box.Width??220;
            for(var i=0;i<links.Length;i++)
            {
                var s=links[i]; g.Nodes.Add(new("service:"+s.Id,MapKind.Service,"",new(lx,y+i*56,linkWidth,46),ShortService(s),"Bookmark · not probed",s)
                {Online=false,Match=Match(s.Name,s.Url),Endpoints=[s]});
            }
            bottom=Math.Max(bottom,y+links.Length*56);
        }
        if(options.Checks)
        {
            float y=bottom+34; g.Labels.Add(new("UPTIME / INDEPENDENT MONITOR HISTORY",16,y)); y+=24;
            for(var i=0;i<snapshot.Monitors.Count;i++)
            {
                var m=snapshot.Monitors[i]; var cols=Math.Max(1,(int)(g.Width/230));
                g.Nodes.Add(new("monitor:"+m.Id,MapKind.Monitor,"",new(16+(i%cols)*230,y+(i/cols)*64,212,54),m.Name,UptimePercent(m.Uptime24h)+" / 24h",m)
                {Online=m.Status==1&&!snapshot.IsStale,Match=Match(m.Name)});
            }
            bottom=y+(float)Math.Ceiling(snapshot.Monitors.Count/(double)Math.Max(1,(int)(g.Width/230)))*64;
        }
        g.Height=bottom+24;
        return g;
    }
    public static string ShortHost(LabDevice d) => d.Name;
    public static string ShortService(LabService s) => s.Container switch {"uptime-kuma"=>"Uptime Kuma","nodered"=>"Node-RED","pingvin-share"=>"Pingvin Share","homeassistant"=>"Home Assistant","homepage"=>"Homepage","jellyfin"=>"Jellyfin","torrserver"=>"TorrServer",_=>s.Name.Split('·')[0].Trim()};
    public static string ServiceState(LabService s) => s.Status=="Healthy"?"Reachable":s.Status;
    // Uptime Kuma's uptimeList uses fractions (1 = 100%), unlike OS CPU/RAM percentages.
    public static string UptimePercent(double? fraction) => fraction is >=0 and <=1?(fraction.Value*100).ToString("0.0")+"%":"Not reported";
    public static string ModelName(string? model) => string.IsNullOrEmpty(model)?"Model not reported":model.Split('/').Last();
    public static string Count(double? n) => n is null?"—":n>=1e6?(n/1e6)?.ToString("0.0")+"m":n>=1e3?(n/1e3)?.ToString("0.0")+"k":n.Value.ToString("N0");
    public static string Bytes(double b) => b>=1099511627776?(b/1099511627776).ToString("0.0")+" TiB":b>=1073741824?(b/1073741824).ToString("0.0")+" GiB":(b/1048576).ToString("0")+" MiB";
}
