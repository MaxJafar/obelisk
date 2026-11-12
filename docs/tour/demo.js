// Browser-only tour. No microphone, storage, service connections, or native actions.
if (!window.__TAURI__) {
 (()=>{
  const listeners=new Set();let fixture,options={services:true,agents:true,activity:true},motion=false,timer;
  const emit=(kind,value)=>listeners.forEach(fn=>fn({payload:{kind,value}}));
  const state={phase:'Standby',online:false,link:'Synthetic demo',detail:'Fictional inventory. No device is connected.',agent:'Jarvis',engine:'Voice motion preview · no audio recorded',preview:true,rgb:false,listening:false,busy:false};
  function render(){const mask=(options.services?1:0)+(options.agents?2:0)+(options.storage?4:0)+(options.activity?8:0)+(options.checks?16:0);
   const graph=structuredClone(fixture.maps[mask+':'+(options.host||'all')]);
   const query=(options.search||'').trim().toLowerCase();for(const n of graph.nodes)n.match=!query||(n.title+' '+n.meta).toLowerCase().includes(query);
   emit('map',graph);
  }
  function config(){emit('configuration',{agentId:'jarvis',voiceEnabled:false,microphones:[],preview:true});}
  function stop(){motion=false;clearInterval(timer);state.phase='Standby';state.listening=false;emit('state',state);emit('audio',{source:'agent',level:0,bands:Array(24).fill(0)});emit('audio',{source:'user',level:0,bands:Array(24).fill(0)});const button=document.getElementById('tour-motion');if(button)button.textContent='Preview voice motion ↗';}
  window.ObeliskDemo={event:{listen:async(_,fn)=>{listeners.add(fn);return()=>listeners.delete(fn);}},core:{invoke:async(_,args)=>{
   const c=args.message;
   if(c.op==='init'){
    await new Promise((resolve,reject)=>{const script=document.createElement('script');script.src='demo-data.js';script.onload=resolve;script.onerror=()=>reject(new Error('Demo fixture unavailable. Run tools/Build-Demo.ps1.'));document.head.append(script);});fixture=window.ObeliskFixture;
    emit('state',state);render();config();emit('reply',{text:'Your systems, in one place. Explore a device, switch map layers, and inspect a session. This tour uses invented data.',final:true});
    const button=document.createElement('button');button.id='tour-motion';button.className='text-button';button.textContent='Preview voice motion ↗';document.querySelector('.voice-actions').append(button);
    button.onclick=()=>{if(motion){stop();return;}motion=true;let t=0;timer=setInterval(()=>{t+=.05;state.listening=t%10<4;state.phase=state.listening?'Listening':'Speaking';emit('state',state);emit('audio',{source:state.listening?'user':'agent',level:.5,bands:Array.from({length:24},(_,i)=>Math.max(0,Math.sin(t*4+i*.4)*.4+.3)*(.5+.5*Math.sin(t*2)**2))});},50);button.textContent='Stop motion preview ■';};
    const link=document.createElement('a');link.href='https://github.com/MaxJafar/obelisk';link.textContent='Source ↗';link.className='tour-source';document.querySelector('.header-right').prepend(link);
   }else if(c.op==='layers'){options=c;render();}
   else if(c.op==='refresh'){render();emit('notice',{message:'Synthetic fixture refreshed. No network requests to devices.'});}
   else if(c.op==='detail'){const d=structuredClone(fixture.details[c.id]);if(!d)return;const size=c.pageSize||3;d.count=d.sessions.length;d.pages=Math.max(1,Math.ceil(d.count/size));d.page=Math.max(0,Math.min(c.page||0,d.pages-1));d.sessions=d.sessions.slice(d.page*size,(d.page+1)*size);emit('detail',d);}
   else if(c.op==='configuration')config();
   else if(c.op==='stop')stop();
   else if(c.op==='visible'){if(!c.value)stop();}
   else emit('notice',{message:'This is a synthetic tour. Connect your own devices in the native app.'});
  }}};
 })();
}
