using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Trial;
internal static class RealCaptureChecks
{
 public static async Task Run(string output)
 {
  Directory.CreateDirectory(output);
  var target=NativeInjection.FindGames().Single(g=>g.ContentId.Length>0);
  if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target.Path))).Equals(GameProfile.Load().ImageSha256,StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected game image");
  using var game=new GameLink();
  // Test-only identity fixture: capture uses the real HWND without attaching another game hook.
  typeof(GameLink).GetField("identity",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(game,new GameIdentity(target.Character,GameNames.World(target.World.ToString()),target.ContentId,target.Pid,true));
  var settings=new Settings{HardwareStream=true,StreamBringToFront=false,StreamHardwareHelper=Path.GetFullPath("work/standalone-trial/artifacts/stream-host/StreamHost.exe"),StreamEncoderPath=Path.Combine(Environment.GetEnvironmentVariable("FF14_TRIAL_COMPONENTS")??Path.GetFullPath("components"),"ffmpeg","ffmpeg.exe")};
  using var capture=new GameCapture(game,_=>true,settings);
  foreach(bool cpu in new[]{false,true}){
   settings.HardwareStream=!cpu;var response=JsonSerializer.SerializeToElement(await capture.Action(JsonSerializer.SerializeToElement(new{action="start",fps=30,bitrate=2000000}),"capture-check",CancellationToken.None));
   Console.WriteLine("START "+response);using var wire=new MemoryStream();using var timeout=new CancellationTokenSource(3500);
   try{await capture.Video(wire,"capture-check",response.GetProperty("session").GetString()!,()=>true,timeout.Token);}catch(OperationCanceledException){}
   var bytes=wire.ToArray();int start=Encoding.ASCII.GetString(bytes).IndexOf("\r\n\r\n",StringComparison.Ordinal)+4;if(start<4)throw new Exception("No HTTP headers");int frames=0;using var raw=new MemoryStream();byte[]? first=null;
   while(start+4<=bytes.Length){int size=BitConverter.ToInt32(bytes,start);start+=4;if(size<4||start+size>bytes.Length)break;var frame=bytes.AsSpan(start,size).ToArray();if(first==null&&PhoneUdpVideoSession.IsKey(frame))first=frame;raw.Write(frame);start+=size;frames++;}
   if(frames<2||first==null)throw new Exception("No decodable keyframes");string name=cpu?"real-cpu":"real-tcp";File.WriteAllBytes(Path.Combine(output,name+".h264"),raw.ToArray());if(!cpu)File.WriteAllBytes(Path.Combine(output,"first-key.h264"),first);
   Console.WriteLine(name+" frames="+frames+" bytes="+raw.Length);capture.Stop();
  }
  settings.HardwareStream=true;settings.StreamTransport="udp";settings.StreamUdpPort=19879;
  var udp=JsonSerializer.SerializeToElement(await capture.Action(JsonSerializer.SerializeToElement(new{action="start",fps=30,bitrate=2000000}),"capture-check",CancellationToken.None));
  File.WriteAllText(Path.Combine(output,"udp-info.json"),JsonSerializer.Serialize(new{port=udp.GetProperty("udpPort").GetInt32(),key=udp.GetProperty("udpKey").GetString()}));
  var java=new ProcessStartInfo(Environment.GetEnvironmentVariable("FF14_TRIAL_JAVA")??"java"){UseShellExecute=false,CreateNoWindow=true};foreach(string arg in new[]{"-cp",Path.GetFullPath("work/standalone-trial/tests/runtime/java-video"),"cn.xiaoyanjiu.ff14chat.trial.StreamProtocolChecks",output,udp.GetProperty("udpPort").GetInt32().ToString(),udp.GetProperty("udpKey").GetString()!})java.ArgumentList.Add(arg);
  using var consumer=Process.Start(java)!;await consumer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));if(consumer.ExitCode!=0)throw new Exception("Real game UDP/Java consumer failed");
  Console.WriteLine("REAL CAPTURE TCP/CPU/UDP CHECK PASSED; no injection or input.");
 }
}
