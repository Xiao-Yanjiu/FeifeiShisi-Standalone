using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
namespace Trial;
internal static class OriginalInputChecks
{
 public static int Run()
 {
  var events=new List<string>();int count=0;
  void Check(bool ok,string name){if(!ok)throw new Exception("Original input: "+name);count++;}
  using var input=new OriginalStreamInput(a=>a=="owner");
  void Set(string field,object value)=>typeof(OriginalStreamInput).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(input,value);
  Set("streamFocusProbe",(Func<nint>)(()=>1));Set("streamKeyWriter",(Action<ushort,bool>)((k,d)=>events.Add($"key:{k}:{d}")));
  Set("streamTextWriter",(Action<string>)(t=>events.Add("text:"+t)));Set("streamWheelWriter",(Action<int>)(n=>events.Add("wheel:"+n)));
  Set("streamPointerWriter",(Action<nint,float,float,bool>)((w,x,y,d)=>events.Add("pointer:"+d)));
  Set("streamPointerMover",(Action<nint,float,float>)((w,x,y)=>events.Add("move")));
  Set("streamRelativeMover",(Action<nint,float,float>)((w,x,y)=>events.Add("relative")));
  Set("streamPointerButtonWriter",(Action<bool>)(d=>events.Add("button:"+d)));
  using var process=Process.GetCurrentProcess();input.Attach(process,1,"owner","session");
  JsonElement Data(object value)=>JsonSerializer.SerializeToElement(value);
  void Apply(string route,object value,string auth="owner")=>input.Apply(route,Data(value),auth);
  void Reject(Action action,string name){bool failed=false;try{action();}catch{failed=true;}Check(failed,name);}
  Reject(()=>Apply("/stream-control",new{session="session",sequence=1,enabled=true},"other"),"owner isolation");
  Apply("/stream-control",new{session="session",sequence=1,enabled=true});
  Apply("/stream-input",new{session="session",control=1,sequence=1,keys=new[]{"W"},wheelUp=0,wheelDown=0});Check(events.Contains("key:87:True"),"original key mapping");
  int before=events.Count;Apply("/stream-input",new{session="session",control=1,sequence=1,keys=new[]{"S"},wheelUp=0,wheelDown=0});Check(events.Count==before,"duplicate input ignored");
  Reject(()=>Apply("/stream-input",new{session="session",control=0,sequence=2,keys=new[]{"S"},wheelUp=0,wheelDown=0}),"stale control epoch");
  Apply("/stream-input",new{session="session",control=1,sequence=2,keys=Array.Empty<string>(),wheelUp=1,wheelDown=0});Check(events.Contains("key:87:False")&&events.Contains("wheel:1"),"key release and wheel");
  Reject(()=>Apply("/stream-input",new{session="session",control=1,sequence=3,keys=Array.Empty<string>(),wheelUp=100,wheelDown=0}),"wheel bound");
  Reject(()=>Apply("/stream-input",new{session="session",control=1,sequence=4,keys=new[]{"UNKNOWN"},wheelUp=1,wheelDown=0}),"unknown keys rejected");
  Apply("/stream-pointer",new{session="session",control=1,sequence=1,x=.4f,y=.4f,down=true});Check(events.Contains("pointer:True"),"original pointer press");
  Apply("/stream-control",new{session="session",sequence=2,enabled=false});Check(events.Contains("pointer:False"),"disabling releases pointer");
  Reject(()=>Apply("/stream-pointer",new{session="session",control=1,sequence=2,x=.4f,y=.4f,down=true}),"old pointer rejected");
  Apply("/stream-ime",new{session="session",sequence=1,text="中文输入",delete=0});Check(events.Contains("text:中文输入"),"original IME preserved");
  Reject(()=>Apply("/stream-ime",new{session="other",sequence=2,text="错误",delete=0}),"IME session guard");
  Apply("/stream-control",new{session="session",sequence=3,enabled=true});
  Apply("/stream-input",new{session="session",control=3,sequence=5,keys=new[]{"NUM8"},wheelUp=0,wheelDown=0});
  Set("streamKeyTouched",Environment.TickCount64-1000);input.Tick();Check(events.Contains("key:104:False"),"lost heartbeat releases key");
  input.Close();Reject(()=>Apply("/stream-control",new{session="session",sequence=4,enabled=true}),"closed session rejected");
  return count;
 }
}
