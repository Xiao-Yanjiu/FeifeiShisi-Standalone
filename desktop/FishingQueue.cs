using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core=OriginalDesktop.PhoneChatCore;
namespace Trial;
internal sealed record FishReminder(string Key,string Title,string Body,long At,long Expires);
internal sealed record FishNotice(long id,string key,string title,string body,long expires);
internal static class NoticeIds {static long last; public static long Next(){while(true){long old=Interlocked.Read(ref last),next=Math.Max(old+1,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*1000);if(Interlocked.CompareExchange(ref last,next,old)==old)return next;}}}
// Host lifecycle/persistence adapter; validation, plans and deduplication remain the original FishSchedule.
internal sealed class FishingQueue:IDisposable
{
 public readonly object Gate=new();
 public Core.FishSchedule Schedule {get;private set;}=new();
 readonly Dictionary<string,List<FishNotice>> notices=new();
 readonly System.Threading.Timer timer;
 readonly string file=Path.Combine(Log.Root,"fishing.json");
 public string Status {get;private set;}="等待手机同步提醒";
 public Func<string,Core.FishRow,int,Task<bool>>? ExternalDelivery;
 public Func<string,bool>? DirectAvailable;
 public FishingQueue(){try{if(File.Exists(file)){if(new FileInfo(file).Length>8000000)throw new IOException("提醒配置过大");Schedule=JsonSerializer.Deserialize<Core.FishSchedule>(File.ReadAllText(file))??new();}}catch(Exception e){Status="提醒配置无法读取："+e.Message;}timer=new(_=>Tick(),null,1000,1000);}
 internal static string Owner(string auth)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth)));
 void Save(){Directory.CreateDirectory(Log.Root);var temp=file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(Schedule));File.Move(temp,file,true);}
 public object Action(JsonElement r,string auth)
 {
  string owner=Owner(auth),action=r.GetProperty("action").GetString()??"";long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  lock(Gate){
   if(action=="sync"){Schedule.Replace(owner,r.GetProperty("rows"),!r.TryGetProperty("enabled",out var enabled)||enabled.GetBoolean(),r.TryGetProperty("mode",out var route)?route.GetInt32():0,now);Save();Status="提醒计划已同步";}
   else if(action=="test"){var row=new Core.FishRow{key="test:"+Guid.NewGuid().ToString("N"),title="鱼糕测试通知",body="这是来自鱼糕的服务端测试通知！",at=now,expires=now+900000};Dispatch(owner,row,Schedule.Get(owner).mode);return new{ok=true,testNotice=new{row.key,row.title,row.body}};}
   else if(action=="notify"){string Text(string key,int max){string value=r.GetProperty(key).GetString()??"";if(value.Length>max||value.Any(char.IsControl))throw new InvalidDataException("提醒内容无效");return value;}Dispatch(owner,new Core.FishRow{key=Guid.NewGuid().ToString("N"),title=Text("title",120),body=Text("body",500),at=now,expires=now+900000},Schedule.Get(owner).mode);}
   else if(action!="status")throw new InvalidDataException("未知提醒操作");
   var plan=Schedule.Get(owner);return new{ok=true,count=plan.rows.Count,next=plan.rows.FirstOrDefault()?.at??0,status=Status};
  }
 }
 void Dispatch(string owner,Core.FishRow row,int mode)
 {
  if(mode==1||(mode==0&&(DirectAvailable==null||DirectAvailable(owner)))){Add(owner,row);return;}
  var deliver=ExternalDelivery;if(deliver==null){if(mode==0)Add(owner,row);else Status="尚未配置 QQ／推送服务";return;}
  _=Task.Run(async()=>{try{bool ok=await deliver(owner,row,mode);lock(Gate){if(!ok&&mode==0)Add(owner,row);Status=ok?"鱼糕提醒已交给推送服务":"外部提醒未发送，请检查推送设置";}}catch(Exception e){lock(Gate){if(mode==0)Add(owner,row);Status="提醒发送失败："+e.Message;}}});
 }
 void Add(string owner,Core.FishRow row){if(!notices.TryGetValue(owner,out var rows))notices[owner]=rows=new();rows.Add(new(NoticeIds.Next(),row.key,row.title,row.body,row.expires));while(rows.Count>200)rows.RemoveAt(0);}
 void Tick(){try{lock(Gate){var due=Schedule.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());foreach(var row in due)Dispatch(row.owner,row.row,row.mode);if(due.Count>0)Save();}}catch(Exception e){Status="提醒处理失败："+e.Message;Log.Write(e);}}
 public FishNotice[] Due(string auth,long after){Tick();lock(Gate){if(!notices.TryGetValue(Owner(auth),out var rows))return [];long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();rows.RemoveAll(r=>r.expires<now);return rows.Where(r=>r.id>after).ToArray();}}
 public void Dispose()=>timer.Dispose();
}
