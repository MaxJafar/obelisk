#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
use std::{io::{BufRead, BufReader, Write}, process::{Child,ChildStdin,Command,Stdio}, sync::Mutex};
use std::os::windows::process::CommandExt;
use tauri::{Emitter,Manager};
struct Backend { input:Mutex<Option<ChildStdin>>, child:Mutex<Option<Child>> }
#[tauri::command]
fn command(message:serde_json::Value, backend:tauri::State<Backend>)->Result<(),String>{
    let text=serde_json::to_string(&message).map_err(|e|e.to_string())?;
    if text.len()>65536{return Err("Command is too large".into())}
    let mut input=backend.input.lock().map_err(|_|"Backend unavailable")?;
    let pipe=input.as_mut().ok_or("Backend stopped")?;
    writeln!(pipe,"{text}").and_then(|_|pipe.flush()).map_err(|_|"Native backend disconnected".into())
}
fn main(){
 let app=tauri::Builder::default().invoke_handler(tauri::generate_handler![command]).setup(|app|{
    let packaged=app.path().resource_dir()?.join("bridge/ObeliskBridge.exe");
    let development=std::env::current_exe()?.ancestors().find_map(|path| {
        let candidate=path.join("resources/bridge/ObeliskBridge.exe");
        candidate.is_file().then_some(candidate)
    });
    let binary=if packaged.is_file(){packaged}else{development.ok_or("Build the native bridge first with Build-Obelisk.ps1")?};
    let mut launch=Command::new(binary);launch.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null()).creation_flags(0x08000000);
    if std::env::args().any(|a|a=="--preview"){launch.arg("--preview");}
    let mut child=launch.spawn()?;let input=child.stdin.take();let output=child.stdout.take().unwrap();
    app.manage(Backend{input:Mutex::new(input),child:Mutex::new(Some(child))});
    let handle=app.handle().clone();
    std::thread::spawn(move||{for line in BufReader::new(output).lines(){match line {Ok(line)=>if let Ok(value)=serde_json::from_str::<serde_json::Value>(&line){let _=handle.emit("native",value);},Err(_)=>break}} let _=handle.emit("native",serde_json::json!({"kind":"notice","value":{"message":"Native backend closed. Restart Obelisk to reconnect."}}));});
    Ok(())
 }).build(tauri::generate_context!()).expect("Could not start Obelisk");
 app.run(|handle,event|if matches!(event,tauri::RunEvent::Exit){
    let backend=handle.state::<Backend>();backend.input.lock().unwrap().take();
    if let Some(mut child)=backend.child.lock().unwrap().take(){
      for _ in 0..40 {if child.try_wait().ok().flatten().is_some(){return}std::thread::sleep(std::time::Duration::from_millis(50));}
      let _=child.kill();let _=child.wait();
    }
 });
}
