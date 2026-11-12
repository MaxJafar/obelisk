const $ = id => document.getElementById(id);
const native = window.__TAURI__ || window.ObeliskDemo;
const send = (op, data={}) => native.core.invoke('command', {message:{op,...data}}).catch(e=>notice(String(e)));
function notice(message){$('notice').textContent=message;}
function detail(id,page=0){return send('detail',{id,page,pageSize:Math.max(1,Math.min(3,Math.floor(($('map-stage').clientHeight-330)/70)))});}
let inspected=null, detailTab='overview', endpointPage=0;
let state={}, map=null, selected=null, page=0, replyText='', replyPage=0, view=null, initial=true, pendingFit=true;
const layers={services:true,agents:true,storage:false,activity:true,checks:false,search:'',host:null};
const spectra={agent:Array(24).fill(0),user:Array(24).fill(0)}, lastAudio={agent:0,user:0}, levels={agent:0,user:0};
function elapsed(ms){return ms==null?'—':(ms/1000).toFixed(2)+' s';}
function onNative({kind,value:v}){
 if(kind==='state'){
  state=v;$('phase').textContent=v.phase.toUpperCase();$('gateway').textContent=v.online?'Gateway connected':v.link;
  $('gateway').title=v.detail;$('gateway-dot').classList.toggle('off',!v.online);
  $('engine-status').textContent=v.engine;$('agent-name').textContent=v.agent+' / SYSTEM OPERATOR';
  $('mic-state').textContent=v.listening?'LISTENING':'MIC CLOSED';$('mic').querySelector('span').textContent=v.listening?'Send voice':v.busy?'Interrupt & speak':'Speak';
  $('mic').disabled=v.preview||!v.online||v.phase==='Stopping';$('composer').querySelector('button').disabled=v.preview||v.busy||!v.online;
  $('warm').disabled=v.preview;$('rgb').disabled=v.preview;
  $('rgb').checked=v.rgb;$('stt-time').textContent=elapsed(v.sttMs);$('first-time').textContent=elapsed(v.firstPlaybackMs);
  if(v.preview){$('footer-mode').textContent='DEMO · synthetic data';$('gateway').textContent='Demo / no connections';}
 } else if(kind==='audio'){
  if(v.source in spectra){spectra[v.source]=v.bands;levels[v.source]=v.level;lastAudio[v.source]=performance.now();}
 } else if(kind==='map'){map=v;renderMap();if(inspected&&!$('inspector').hidden)detail(inspected.id,page);}
 else if(kind==='detail')renderDetail(v);
 else if(kind==='reply'){replyText=v.text;replyPage=Math.max(0,replyPages().length-1);renderReply();}
 else if(kind==='transcript'||kind==='partial'){notice((kind==='partial'?'Hearing: ':'You: ')+v.text);}
 else if(kind==='configuration')renderConfiguration(v);
 else if(kind==='notice'){notice(v.message);if($('settings-dialog').open)$('setup-error').textContent=v.message;}
}
native.event.listen('native',event=>onNative(event.payload)).then(()=>send('init'));
$('clock').textContent=new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'});
setInterval(()=>{$('clock').textContent=new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'});},1000);
$('composer').addEventListener('submit',e=>{e.preventDefault();if(state.busy||state.preview||!state.online)return;const text=$('message').value.trim();if(text){send('send',{text});$('message').value='';replyText='';replyPage=0;renderReply();}});
$('message').addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();$('composer').requestSubmit();}});
$('mic').onclick=()=>send('mic');$('stop').onclick=()=>send('stop');$('warm').onclick=()=>send('warm');
$('rgb').onchange=e=>send('rgb',{value:e.target.checked});$('refresh').onclick=()=>send('refresh');
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&!$('settings-dialog').open){send('stop');$('inspector').hidden=true;}if(e.ctrlKey&&e.shiftKey&&e.code==='Space'&&!$('settings-dialog').open){e.preventDefault();send('mic');}});
document.addEventListener('visibilitychange',()=>send('visible',{value:!document.hidden}));
for(const button of $('layers').children)button.onclick=()=>{const key=button.dataset.layer;layers[key]=!layers[key];button.setAttribute('aria-pressed',String(layers[key]));pendingFit=true;send('layers',layers);};
let searchTimer;$('search').oninput=e=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{layers.search=e.target.value;send('layers',layers);},180);};
$('back').onclick=()=>{layers.host=null;$('back').hidden=true;$('inspector').hidden=true;pendingFit=true;send('layers',layers);};
function replyPages(){const max=innerHeight<800?190:370;const pages=[];let text=replyText;while(text.length>max){let at=text.lastIndexOf(' ',max);if(at<max/2)at=max;pages.push(text.slice(0,at));text=text.slice(at).trimStart();}pages.push(text);return pages;}
function renderReply(){const pages=replyPages();replyPage=Math.max(0,Math.min(replyPage,pages.length-1));$('reply').textContent=pages[replyPage]||'A local voice. A connected home.';$('reply-page').textContent=String(replyPage+1).padStart(2,'0')+' / '+String(pages.length).padStart(2,'0');$('reply-prev').disabled=replyPage===0;$('reply-next').disabled=replyPage===pages.length-1;}
$('reply-prev').onclick=()=>{replyPage--;renderReply();};$('reply-next').onclick=()=>{replyPage++;renderReply();};
const ns='http://www.w3.org/2000/svg';
function svg(tag,attrs={},text){const e=document.createElementNS(ns,tag);for(const[k,v]of Object.entries(attrs))e.setAttribute(k,String(v));if(text!==undefined)e.textContent=text;return e;}
function fit(){if(!map)return;const width=$('topology').clientWidth,height=$('topology').clientHeight;const ratio=width/height;let w=map.width+28,h=map.height+48;if(w/h<ratio)w=h*ratio;else h=w/ratio;view={x:(map.width-w)/2,y:(map.height-h)/2-10,w,h};applyView();}
function applyView(){if(view)$('topology').setAttribute('viewBox',[view.x,view.y,view.w,view.h].join(' '));}
function zoom(f,x=.5,y=.5){if(!view||!map)return;const nw=view.w*f;if(nw<map.width*.22||nw>map.width*4)return;view.x+=view.w*x*(1-f);view.y+=view.h*y*(1-f);view.w*=f;view.h*=f;applyView();}
$('fit').onclick=fit;$('zoom-in').onclick=()=>zoom(.8);$('zoom-out').onclick=()=>zoom(1.25);
$('topology').addEventListener('wheel',e=>{e.preventDefault();const r=$('topology').getBoundingClientRect();zoom(Math.exp(Math.sign(e.deltaY)*.12),(e.clientX-r.left)/r.width,(e.clientY-r.top)/r.height);},{passive:false});
let drag=null;$('topology').addEventListener('pointerdown',e=>{if(!view||e.target.closest('.node'))return;drag={x:e.clientX,y:e.clientY,vx:view.x,vy:view.y};$('topology').setPointerCapture(e.pointerId);$('topology').classList.add('dragging');});
$('topology').addEventListener('pointermove',e=>{if(drag){view.x=drag.vx-(e.clientX-drag.x)*view.w/$('topology').clientWidth;view.y=drag.vy-(e.clientY-drag.y)*view.h/$('topology').clientHeight;applyView();}});
function endDrag(){drag=null;$('topology').classList.remove('dragging');}$('topology').addEventListener('pointerup',endDrag);$('topology').addEventListener('pointercancel',endDrag);
function renderMap(){
 $('empty-map').hidden=map.nodes.length>0;$('node-count').textContent=map.online+' / '+map.devices;$('agent-count').textContent=map.agents;$('session-count').textContent=map.sessions.toLocaleString();
 $('map-status').textContent=(map.stale?'STALE · ':'')+map.status;
 const g=$('graph');g.replaceChildren();
 for(const f of map.frames){g.append(svg('rect',{x:f.box.x,y:f.box.y,width:f.box.width,height:f.box.height,class:'frame'}));}
 for(let i=0;i<map.edges.length;i++){
  const e=map.edges[i],d=e.points.map((p,j)=>(j?'L':'M')+p.x+' '+p.y).join(' ');g.append(svg('path',{d,class:'edge'+(e.online?'':' off')}));
  if(e.online){const dot=svg('circle',{r:1.8,class:'moving'});dot.style.offsetPath='path("'+d+'")';dot.style.animationDelay=(-i*.7)+'s';g.append(dot);}
 }
 for(const l of map.labels)g.append(svg('text',{x:l.x,y:l.y,class:'graph-label'},l.text));
 for(const n of map.nodes){
  const b=n.box,group=svg('g',{class:'node'+(n.online?'':' off')+(n.match?'':' dim')+(selected===n.id?' selected':''),tabindex:0,role:'button','aria-label':n.title+' '+n.meta,transform:'translate('+b.x+' '+b.y+')'});
  group.append(svg('title',{},n.title+' — '+n.meta));
  group.append(n.kind==='Network'?svg('ellipse',{cx:b.width/2,cy:b.height/2,rx:b.width/2,ry:b.height/2,class:'outline'}):svg('rect',{width:b.width,height:b.height,class:'outline'}));
  const max=Math.floor((b.width-38)/6.5),short=s=>s.length>max?s.slice(0,max-1)+'…':s;
  const titleY=b.height<48?16:24,metaY=Math.min(b.height-11,41);
  group.append(svg('circle',{cx:13,cy:titleY-4,r:2.5,class:'node-state'}));
  group.append(svg('text',{x:24,y:titleY,class:'node-title'},short(n.title)));
  group.append(svg('text',{x:24,y:metaY,class:'node-meta'},short(n.meta||'')));
  if(n.kind==='Device'&&b.height>60)group.append(svg('text',{x:24,y:65,class:'node-meta'},n.online?'CONNECTED  ·  SELECT TO EXPLORE':'UNAVAILABLE'));
  if(n.fraction>=0){group.append(svg('line',{x1:24,y1:b.height-4,x2:b.width-14,y2:b.height-4,class:'fraction-track'}));group.append(svg('line',{x1:24,y1:b.height-4,x2:24+(b.width-38)*Math.min(1,n.fraction),y2:b.height-4,class:'fraction-used'}));}
  const choose=()=>{selected=n.id;page=0;detail(n.id);for(const node of g.querySelectorAll('.selected'))node.classList.remove('selected');group.classList.add('selected');};
  group.onclick=choose;group.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();choose();}};
  group.ondblclick=()=>{if(n.host){layers.host=n.host;pendingFit=true;$('back').hidden=false;send('layers',layers);}};g.append(group);
 }
 if(initial||pendingFit){fit();initial=false;pendingFit=false;}
}
function el(tag,text,cls){const node=document.createElement(tag);if(text!=null)node.textContent=String(text);if(cls)node.className=cls;return node;}
function pair(parent,name,value){if(value==null||value==='')return;const row=el('div',null,'detail-pair');row.append(el('span',name),el('span',value));parent.append(row);}
function bytes(value){if(value==null)return'—';return(value/1073741824).toFixed(1)+' GiB';}
function uptime(value){if(value==null)return'—';return Math.floor(value/86400)+'d '+Math.floor(value%86400/3600)+'h';}
function renderDetail(d){
 if(inspected?.id!==d.id){detailTab='overview';endpointPage=0;}inspected=d;
 selected=d.id;page=d.page;$('inspector').hidden=false;$('detail-title').textContent=d.title;$('detail-kind').textContent=d.kind.toUpperCase();const body=$('detail-body');body.replaceChildren();const data=d.data||{};
 const tabs=$('detail-tabs');tabs.replaceChildren();if(d.kind==='Device'){for(const[key,label]of[['overview','Overview'],['services','Services'],['sessions','Sessions']]){const b=el('button',label);b.setAttribute('aria-pressed',String(detailTab===key));b.onclick=()=>{detailTab=key;renderDetail(d);};tabs.append(b);}}
 if(d.kind==='Device'&&detailTab==='overview'){
  pair(body,'Address',data.ip);pair(body,'System',data.os);pair(body,'Uptime',uptime(data.uptimeSeconds));pair(body,'CPU',data.cpuPercent==null?'—':data.cpuPercent.toFixed(1)+'%');pair(body,'Memory',data.memoryPercent==null?'—':data.memoryPercent.toFixed(1)+'%');
  const focus=el('button','Explore device ↗');focus.className='text-button';focus.onclick=()=>{layers.host=data.id;pendingFit=true;$('back').hidden=false;$('inspector').hidden=true;send('layers',layers);};body.append(focus);
 } else if(d.kind==='Agent'){if(innerHeight>=800)pair(body,'Provider',data.provider);pair(body,'Model',data.model);if(innerHeight>=800)pair(body,'Sessions',data.sessions);}
 else if(d.kind==='Volume'){pair(body,'Mount',data.mount);pair(body,'Capacity',bytes(data.totalBytes));pair(body,'Used',bytes(data.usedBytes));pair(body,'Free',bytes(data.freeBytes));}
 else if(d.kind==='Monitor'){pair(body,'Status',data.status===1?'Up':'Unavailable');pair(body,'Uptime / 24h',data.uptime24h==null?'—':(data.uptime24h*100).toFixed(2)+'%');pair(body,'Latency',data.latencyMs==null?'—':data.latencyMs+' ms');}
 else if(d.kind==='Session'){for(const[name,key]of[['State','state'],['Model','model'],['Tokens','totalTokens'],['Input','inputTokens'],['Output','outputTokens'],['Cache read','cacheReadTokens'],['Tool calls','toolCalls']])pair(body,name,data[key]);}
 else if(d.kind==='Network'){pair(body,'Network','Private Tailscale mesh');pair(body,'Nodes',map.online+' online');}
 const showEndpoints=d.kind!=='Device'||detailTab==='services',showSessions=d.kind!=='Device'||detailTab==='sessions';
 const endpointSize=Math.max(1,Math.floor(($('map-stage').clientHeight-270)/80)),endpointPages=Math.max(1,Math.ceil(d.endpoints.length/endpointSize));endpointPage=Math.min(endpointPage,endpointPages-1);
 if(showEndpoints&&d.endpoints.length){body.append(el('div','SERVICE ENDPOINTS','detail-sub'));for(const item of d.endpoints.slice(endpointPage*endpointSize,(endpointPage+1)*endpointSize)){const s=item.service,row=el('div',null,'service-row');row.append(el('span',s.name+' · '+s.status));const open=el('button','Open ↗');open.onclick=()=>send('open',{id:s.id});row.append(open);if(item.canControl){const toggle=el('button',s.containerRunning?'Stop service':'Start service');toggle.onclick=()=>{toggle.disabled=true;send('control',{id:s.id,running:!s.containerRunning});};row.append(toggle);}body.append(row);}}
 if(showSessions&&d.sessions.length){body.append(el('div','SESSIONS / '+d.count,'detail-sub'));for(const s of d.sessions){const row=el('div',null,'session-row');row.append(el('b',s.title||s.id),el('small',(s.state||'Unknown')+' · '+(s.model||'Model unknown')),el('small',(s.totalTokens==null?'Usage unavailable':s.totalTokens.toLocaleString()+' tokens')+' · '+s.usageKind));row.title=s.detail;body.append(row);}}
 const paging=$('detail-paging');paging.replaceChildren();
 const sessionPaging=showSessions&&d.pages>1,servicePaging=showEndpoints&&endpointPages>1;
 if(sessionPaging||servicePaging){const current=sessionPaging?d.page:endpointPage,total=sessionPaging?d.pages:endpointPages;const prev=el('button','←'),next=el('button','→');prev.disabled=current===0;next.disabled=current+1===total;const move=delta=>{if(sessionPaging)detail(d.id,d.page+delta);else{endpointPage+=delta;renderDetail(d);}};prev.onclick=()=>move(-1);next.onclick=()=>move(1);paging.append(prev,el('span',(current+1)+' / '+total),next);}
}
$('close-detail').onclick=()=>{$('inspector').hidden=true;};
const orb=$('orb'),line=$('voice-line'),oc=orb.getContext('2d'),lc=line.getContext('2d');let motion=0,lastFrame=0;const reduced=matchMedia('(prefers-reduced-motion: reduce)');
function sizeCanvas(c){const r=c.getBoundingClientRect(),d=Math.min(devicePixelRatio||1,2);if(c.width!==Math.round(r.width*d)||c.height!==Math.round(r.height*d)){c.width=Math.round(r.width*d);c.height=Math.round(r.height*d);}const ctx=c.getContext('2d');ctx.setTransform(d,0,0,d,0,0);return[r.width,r.height];}
function draw(t){requestAnimationFrame(draw);if(document.hidden||t-lastFrame<1000/30)return;const dt=Math.min((t-lastFrame)/1000,.05);lastFrame=t;motion+=reduced.matches?0:dt;
 for(const source of ['agent','user'])if(t-lastAudio[source]>180){levels[source]*=.8;spectra[source]=spectra[source].map(x=>x*.8);}
 const[w,h]=sizeCanvas(orb),cx=w/2,cy=(h-45)/2,r=Math.min(w*.26,cy-41);oc.clearRect(0,0,w,h);oc.strokeStyle='#242725';oc.fillStyle='#242725';
 for(let i=0;i<4;i++){oc.lineWidth=i===0?1.4:.5;oc.globalAlpha=i===0?1:.38;oc.beginPath();oc.ellipse(cx,cy,r+12+i*9,(r+12+i*9)*(i===2?.92:1),i===2?-.3:0,0,Math.PI*2);oc.stroke();}
 oc.globalAlpha=1;oc.beginPath();for(let i=0;i<=192;i++){const angle=i/192*Math.PI*2,b=spectra.agent[Math.floor(i/192*24)%24]||0,amp=(state.phase==='Speaking'?b:0)*10,rad=r+amp*Math.sin(i*1.7+motion*3);const x=cx+Math.cos(angle)*rad,y=cy+Math.sin(angle)*rad;if(i===0)oc.moveTo(x,y);else oc.lineTo(x,y);}oc.closePath();oc.lineWidth=1.6;oc.stroke();
 for(let i=0;i<48;i++){const a=i/48*Math.PI*2;const value=state.phase==='Speaking'?spectra.agent[i%24]||0:0;oc.beginPath();oc.moveTo(cx+Math.cos(a)*(r+5),cy+Math.sin(a)*(r+5));oc.lineTo(cx+Math.cos(a)*(r+7+value*12),cy+Math.sin(a)*(r+7+value*12));oc.lineWidth=i%4===0?.8:.4;oc.stroke();}
 const thinking=state.phase==='Thinking'||state.phase==='Transcribing',speed=thinking?.7:.1;const a=motion*speed;oc.beginPath();oc.arc(cx+Math.cos(a)*(r+31),cy+Math.sin(a)*(r+31),3.2,0,Math.PI*2);oc.fill();
 oc.beginPath();oc.arc(cx,cy,thinking?3+Math.sin(motion*2)*.5:2,0,Math.PI*2);oc.fill();
 const[lw,lh]=sizeCanvas(line);lc.clearRect(0,0,lw,lh);lc.strokeStyle='#242725';lc.lineWidth=.8;for(let j=0;j<3;j++){lc.globalAlpha=j===0?.85:.25;lc.beginPath();for(let x=0;x<=lw;x+=2){const k=Math.min(23,Math.floor(x/lw*24));const env=Math.pow(Math.sin(x/lw*Math.PI),1.5);const v=state.listening?(spectra.user[k]||0):0;const y=lh/2+Math.sin(x*.085+motion*5+j*.8)*v*12*env/(1+j*.3);if(x===0)lc.moveTo(x,y);else lc.lineTo(x,y);}lc.stroke();}lc.globalAlpha=1;
}
new ResizeObserver(()=>{pendingFit=true;if(map)fit();renderReply();if(inspected&&!$('inspector').hidden)detail(inspected.id,0);}).observe($('map-stage'));
requestAnimationFrame(draw);renderReply();

function renderConfiguration(config){
 $('setup-url').value=config.gatewayUrl||'';$('setup-agent').value=config.agentId||'jarvis';
 $('setup-voice').checked=config.voiceEnabled!==false;
 const select=$('setup-mic');select.replaceChildren(new Option('System default',''));
 for(const device of config.microphones||[])select.add(new Option(device.name,device.id));
 select.value=config.micDeviceId||'';
 $('setup-audio').textContent=config.localAudioInstalled?'Local Qwen and both reference voices are installed.':'Local voice needs the optional audio runtime and your two reference recordings. See docs/SETUP.md.';
 $('setup-save').disabled=Boolean(config.preview);
 if(config.preview)$('setup-error').textContent='Demo settings do not save or connect.';
}
$('settings').onclick=()=>{send('configuration');$('setup-error').textContent='';$('settings-dialog').showModal();};
function closeSettings(){$('setup-token').value='';$('setup-speech').value='';$('settings-dialog').close();}
$('settings-close').onclick=closeSettings;
$('settings-dialog').addEventListener('close',()=>{$('setup-token').value='';$('setup-speech').value='';});
$('settings-form').onsubmit=async e=>{e.preventDefault();$('setup-error').textContent='Saving…';await send('configure',{
 gatewayUrl:$('setup-url').value.trim(),agentId:$('setup-agent').value.trim(),gatewayToken:$('setup-token').value,
 speechKey:$('setup-speech').value,micDeviceId:$('setup-mic').value,voiceEnabled:$('setup-voice').checked});
 $('setup-token').value='';$('setup-speech').value='';};
