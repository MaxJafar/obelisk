using System.IO.Pipes;
using System.Net;
using JarvisHud.Audio;

static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
var fft=new AudioSpectrum();
var tone=Enumerable.Range(0,2400).Select(i=>.3f*MathF.Sin(MathF.Tau*440*i/24000)).ToArray();
var features=fft.Process(tone,24000);
Check(features.Level>.5f&&features.Bands.All(float.IsFinite)&&features.Bands.Any(v=>v>.5f),"Actual PCM drives bounded spectrum features");
var silent=fft.Process(new float[480],24000);
Check(silent.Level==0&&silent.Bands.All(v=>v==0),"Silence clears residual FFT history");
var invalid=fft.Process([float.NaN,float.PositiveInfinity],24000);
Check(invalid.Level==0,"Non-finite samples cannot poison the visualizer");
var phrases=new SpeechPhrases();
var first=phrases.Update("The system is ready. Waiting for input",false);
var last=phrases.Update("The system is ready. Waiting for input now.",true);
Check(string.Join(" ",first.Concat(last))=="The system is ready. Waiting for input now.","Streamed phrases preserve order and unspoken revisions");
Check(phrases.Update("The system is ready. Waiting for input now.",true).Count==0,"Repeated final replies do not speak twice");
var revised=new SpeechPhrases();revised.Update("The system is ready. More",false);
try{revised.Update("A changed spoken prefix. More",true);throw new Exception("Accepted a spoken revision");}catch(IOException){Console.WriteLine("PASS Revisions of already spoken text fail explicitly");}
using(var response=new HttpResponseMessage(HttpStatusCode.OK)){
 var pcm=new byte[2400*4];Buffer.BlockCopy(tone,0,pcm,0,pcm.Length);
 var audio=new SpeechAudio(response,new MemoryStream(pcm),CancellationToken.None);
 var output=new StreamingPcm();await output.FeedAsync(audio,CancellationToken.None);
 var read=new byte[pcm.Length];var length=output.Read(read,0,read.Length);
 Check(length==pcm.Length&&read.SequenceEqual(pcm)&&output.UnderrunBytes==0,"PCM transport preserves bytes without artificial underruns");
 Check(output.At(.05).Level>.5f&&output.Read(read,0,read.Length)==0,"Meter follows playback position and completed streams end cleanly");
}
using(var response=new HttpResponseMessage(HttpStatusCode.OK)){
 var audio=new SpeechAudio(response,new MemoryStream(new byte[3]),CancellationToken.None);
 try{await audio.Completion;throw new Exception("Accepted truncated PCM");}catch(IOException){Console.WriteLine("PASS Truncated PCM frames are rejected");}
}
using(var cancel=new CancellationTokenSource()){
 var turn=new LiveSpeechTurn(async(_,ct)=>{await Task.Delay(10000,ct);throw new Exception("Cancellation missed");},(_,_)=>Task.CompletedTask,_=>{},cancel.Token);
 turn.Update("The system is ready for a cancellation test.",true);cancel.Cancel();
 try{await turn.Completion.WaitAsync(TimeSpan.FromSeconds(2));throw new Exception("Cancellation missed");}catch(OperationCanceledException){Console.WriteLine("PASS Cancelling a turn cancels pending inference");}
}
// Protocol peer only: no dependency on the private LightDesk application or hardware.
var pipeName="ObeliskChecks."+Guid.NewGuid().ToString("N");
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
using var peer=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var restored=false;
var server=Task.Run(async()=>{
 await peer.WaitForConnectionAsync(timeout.Token);
 while(true){var frame=new byte[12];await peer.ReadExactlyAsync(frame,timeout.Token);
  Check(frame.AsSpan(0,4).SequenceEqual(new byte[]{76,68,82,49}),"Lighting frame uses the documented protocol");
  await peer.WriteAsync(new byte[]{1},timeout.Token);
  if(frame[4]==0){restored=true;return;}received.TrySetResult();
 }
});
using(var client=new LightDeskSync(pipeName)){
 client.Start();client.Feed(.8f);await received.Task.WaitAsync(timeout.Token);
 client.Stop();await client.Completion.WaitAsync(timeout.Token);await server;
 Check(restored&&client.Status=="Lighting restored","Stopping requests restoration and waits for acknowledgment");
}
Console.WriteLine("Audio checks complete; no microphone, speakers, model, account or real lights used.");
