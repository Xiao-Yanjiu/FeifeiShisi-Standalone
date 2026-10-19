// SPDX-License-Identifier: GPL-3.0-or-later
// Extracted from the original PhoneChat module; business code retained verbatim.
using System;using System.Collections;using System.Collections.Concurrent;using System.Collections.Generic;using System.IO;using System.Linq;using System.Net;using System.Net.Http;using System.Text;using System.Text.Json;using System.Text.RegularExpressions;using System.Threading;using System.Threading.Tasks;
namespace OriginalDesktop;
internal partial class PhoneChatCore {
    public sealed class ChatEntry
    {
        public long id { get; set; }
        public string channel { get; set; } = "";
        public string sender { get; set; } = "";
        public string senderIdentity { get; set; } = "";
        public string originalText { get; set; } = "";
        public string target { get; set; } = "";
        public string text { get; set; } = "";
        public string time { get; set; } = "";
        public string recordSource { get; set; } = "";
        public string recordKey { get; set; } = "";
        public bool liveSeen { get; set; }
        public bool outgoing { get; set; }
        public bool notify { get; set; }
        public bool externalPush { get; set; }
        public ushort kind { get; set; }
        public string[] tabs { get; set; } = Array.Empty<string>();
        public string color { get; set; } = "";
        public int displayVersion { get; set; }
        public object[] parts { get; set; } = Array.Empty<object>();
        public object[] senderParts { get; set; } = Array.Empty<object>();
        public int worldAlias { get; set; }
        public uint statusIcon { get; set; }
        public RecruitmentLink recruitment { get; set; }
        public string historyKey { get; set; } = "";
        public byte sourceKind { get; set; }
        public byte targetKind { get; set; }
        public string systemOrigin { get; set; } = "unknown";
    }

    public sealed class QQReplayGate{
        private readonly string path;private readonly HashSet<string> seen=new();private readonly Queue<string> order=new();private long since;public bool Healthy{get;private set;}=true;
        public QQReplayGate(string path){this.path=path;try{if(File.Exists(path)){if(new FileInfo(path).Length>524288)throw new IOException("去重记录过大");foreach(var key in JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))??Array.Empty<string>()){if(!Regex.IsMatch(key,"^[a-f0-9]{64}$"))throw new IOException("去重记录无效");if(seen.Add(key))order.Enqueue(key);}}}catch{Healthy=false;}}
        public void BeginSession(DateTimeOffset at){lock(seen)since=(at.ToUnixTimeMilliseconds()+999)/1000;}
        public bool IsFreshTimestamp(long sentAt,DateTimeOffset now){lock(seen)return Healthy&&since>0&&sentAt>=since&&sentAt<=now.ToUnixTimeSeconds()+5;}
        public bool TryAccept(string bot,uint group,string messageId,long sentAt,DateTimeOffset now){lock(seen){if(!IsFreshTimestamp(sentAt,now)||!Regex.IsMatch(messageId??"","^-?[0-9]{1,20}$")||messageId=="0")return false;string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bot+"|"+group+"|"+messageId))).ToLowerInvariant();if(!seen.Add(key))return false;order.Enqueue(key);while(order.Count>4096)seen.Remove(order.Dequeue());try{Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(order.ToArray()));File.Move(temp,path,true);return true;}catch{Healthy=false;return false;}}}
    }

    public sealed class PushProfile {public int provider=-1;public bool qqEnabled;public int mode;public int[] priority;public bool direct;public string wx="",server="",qq="";}

    public sealed class PushResult {public bool ok;public string status="";}

    public sealed class PushRouter : IDisposable
    {
        private readonly Func<PushProfile> configuration;
        private readonly Action<string> status;
        private readonly Action<ChatEntry> directFallback;
        private readonly Func<int,string,string,CancellationToken,Task<PushResult>> send;
        private readonly Func<DateTime> clock;
        private readonly HttpClient http;
        private readonly Func<string,string,CancellationToken,Task<PushResult>> qqSend;
        private readonly object sync=new();private readonly Queue<(ChatEntry entry,int test,bool fallback)> jobs=new();
        private readonly ConcurrentDictionary<string,DateTime> cooling=new();
        private readonly SemaphoreSlim signal=new(0);private readonly CancellationTokenSource stop=new();private readonly Task worker;
        public PushRouter(Func<PushProfile> configuration,Action<string> status,Action<ChatEntry> directFallback,Func<int,string,string,CancellationToken,Task<PushResult>> transport=null,Func<DateTime> clock=null,bool start=true,Func<string,string,CancellationToken,Task<PushResult>> qqSend=null){this.qqSend=qqSend;this.configuration=configuration;this.status=status;this.directFallback=directFallback;this.clock=clock??(()=>DateTime.UtcNow);http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};send=transport??SendHttp;worker=start?Task.Run(Work):Task.CompletedTask;}
        public static bool ValidWx(string key)=>Regex.IsMatch(key??"","^SPT_[A-Za-z0-9_-]{4,250}$");
        public static bool ValidServer(string key)=>Regex.IsMatch(key??"","^(sctp[0-9]{1,20}t[A-Za-z0-9_-]{4,250}|SCT[A-Za-z0-9_-]{4,250})$");
        public static int[] Order(int mode)=>mode switch{1=>new[]{1},2=>new[]{2},3=>new[]{1,2},4=>new[]{2,1},_=>Array.Empty<int>()};
        public static int[] Providers(PushProfile profile)=>profile.provider>=0?new[]{profile.provider,profile.qqEnabled?3:0}.Where(p=>p>=1&&p<=3).Distinct().ToArray():profile.priority==null?Order(profile.mode):profile.priority.Take(3).Where(p=>p>=1&&p<=3).Distinct().ToArray();
        public static bool ValidQQ(string key)=>uint.TryParse(key,out var id)&&id>0;
        public static bool Configured(int p,PushProfile c)=>p==1?ValidWx(c.wx):p==2?ValidServer(c.server):ValidQQ(c.qq);
        public static string Name(int provider)=>provider==1?"WxPusher":provider==2?"Server酱":"个人QQ";
        public bool Enqueue(ChatEntry entry,int test=0,bool suppressDirect=true){var profile=configuration();if(test==0&&profile.direct)return false;if(!(test>0?new[]{test}:Providers(profile)).Any(provider=>Configured(provider,profile))){if(test>0||Providers(profile).Length>0)status("未填写有效推送凭证，保留直连提醒");return false;}lock(sync){if(stop.IsCancellationRequested||jobs.Count>=64){status("推送队列已满或已停止，保留直连提醒");return false;}jobs.Enqueue((new ChatEntry{channel=entry.channel,sender=entry.sender,target=entry.target,text=entry.text,time=entry.time},test,suppressDirect));signal.Release();return true;}}
        private async Task Work(){try{while(!stop.IsCancellationRequested){await signal.WaitAsync(stop.Token);(ChatEntry entry,int test,bool fallback) job;lock(sync){if(jobs.Count==0)continue;job=jobs.Dequeue();}var profile=configuration();if(job.test>0){profile.provider=job.test;profile.qqEnabled=false;profile.priority=new[]{job.test};profile.direct=false;}if(job.test==0&&profile.direct){directFallback(job.entry);continue;}var result=await Deliver(job.entry,profile,stop.Token,job.test>0);status(result.status);if(!result.ok&&job.fallback&&job.test==0&&!stop.IsCancellationRequested)directFallback(job.entry);await Task.Delay(1300,stop.Token);}}catch(OperationCanceledException){}catch{status("推送工作线程停止，请重开模块");}}
        public async Task<PushResult> Deliver(ChatEntry entry,PushProfile profile,CancellationToken token,bool test=false)
        {
            if(profile.direct&&!test)return new PushResult{status="手机直连，使用直连通知"};
            foreach(var expired in cooling.Where(pair=>pair.Value<=clock()).Select(pair=>pair.Key).ToArray())cooling.TryRemove(expired,out _);
            if(cooling.Count>128)cooling.Clear();
            var providers=Providers(profile);if(providers.Length==0)return new PushResult{status="离线推送和 QQ 提醒均未启用"};
            var results=await Task.WhenAll(providers.Select(provider=>DeliverOne(provider,entry,profile,token,test)));
            return new PushResult{ok=results.Any(result=>result.ok),status=string.Join("；",results.Select(result=>result.status))};
        }
        private async Task<PushResult> DeliverOne(int provider,ChatEntry entry,PushProfile profile,CancellationToken token,bool test)
        {
            token.ThrowIfCancellationRequested();string label=Name(provider),key=provider==1?profile.wx:provider==2?profile.server:profile.qq;
            if(!Configured(provider,profile))return new PushResult{status=label+"未配置"};
            string identity=provider+":"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            if(!test&&cooling.TryGetValue(identity,out var until)&&until>clock())return new PushResult{status=label+"暂缓重试"};
            PushResult result;
            try{using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(8));result=provider==3?(qqSend==null?new PushResult{status="QQ 未连接"}:await qqSend(key,(string.IsNullOrWhiteSpace(entry.sender)?"":entry.sender+"：")+entry.text,deadline.Token)):await send(provider,key,Payload(provider,entry),deadline.Token);}
            catch(OperationCanceledException)when(!token.IsCancellationRequested){result=new PushResult{status="接口超时"};}
            catch(OperationCanceledException){throw;}
            catch{result=new PushResult{status="接口连接失败"};}
            if(result.ok){cooling.TryRemove(identity,out _);return new PushResult{ok=true,status=label+"接口已接收"};}
            cooling[identity]=clock().AddMinutes(1);return new PushResult{status=label+"："+result.status};
        }
        public static string Payload(int provider,ChatEntry entry){string title="狒狒食柿 · "+entry.channel;string text=(string.IsNullOrWhiteSpace(entry.sender)?"":entry.sender+"：")+entry.text;string query="channel="+Uri.EscapeDataString(entry.channel)+"&target="+Uri.EscapeDataString(entry.target??"");if(provider==1)return JsonSerializer.Serialize(new{summary=title,content=text,contentType=1,url="xiaoyanjiu-trial://chat?"+query});string link="scopen://cn.xiaoyanjiu.ff14chat.trial?"+query;return JsonSerializer.Serialize(new{title,desp=text+"\n\n[打开狒狒食柿]("+link+")",@short=text.Length>100?text[..100]:text,tags="FF14|"+entry.channel});}
        public static string Endpoint(int provider,string key){if(provider==1){if(!ValidWx(key))throw new ArgumentException("SPT格式无效");return "https://wxpusher.zjiecode.com/api/send/message/simple-push";}if(!ValidServer(key))throw new ArgumentException("SendKey格式无效");var match=Regex.Match(key,"^sctp([0-9]{1,20})t");return match.Success?"https://"+match.Groups[1].Value+".push.ft07.com/send/"+key+".send":"https://sctapi.ftqq.com/"+key+".send";}
        public static PushResult ParseResponse(int provider,int httpStatus,string body){if(httpStatus<200||httpStatus>=300)return new PushResult{status="HTTP "+httpStatus};try{using var response=JsonDocument.Parse(body);if(!response.RootElement.TryGetProperty("code",out var value)||!value.TryGetInt32(out int code))return new PushResult{status="接口响应格式不符"};return new PushResult{ok=code==(provider==1?1000:0),status="接口拒绝（代码 "+code+"）"};}catch{return new PushResult{status="接口响应格式不符"};}}
        private async Task<PushResult> SendHttp(int provider,string key,string payload,CancellationToken token){using var data=JsonDocument.Parse(payload);var fields=data.RootElement.EnumerateObject().ToDictionary(p=>p.Name,p=>(object)p.Value.Clone());if(provider==1)fields["spt"]=key;using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint(provider,key)){Content=new StringContent(JsonSerializer.Serialize(fields),Encoding.UTF8,"application/json")};using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);using var stream=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[2048];int read;while((read=await stream.ReadAsync(buffer,token))>0){if(output.Length+read>16384)return new PushResult{status="接口响应过大"};output.Write(buffer,0,read);}return ParseResponse(provider,(int)response.StatusCode,Encoding.UTF8.GetString(output.ToArray()));}
        public void Dispose(){stop.Cancel();lock(sync)jobs.Clear();http.Dispose();}
    }


    public sealed class FishRow {public string key{get;set;}="";public string title{get;set;}="";public string body{get;set;}="";public long at{get;set;}public long expires{get;set;}}

    public sealed class FishPlan {public bool enabled{get;set;}=true;public int mode{get;set;}public List<FishRow> rows{get;set;}=new();public Dictionary<string,long> sent{get;set;}=new();}

    // Data only: schedules are supplied by the original fishing page, never recomputed from copied site code.
    public sealed class FishSchedule {
        public Dictionary<string,FishPlan> plans{get;set;}=new();
        public static List<FishRow> Validate(JsonElement raw,long now){
            if(raw.ValueKind!=JsonValueKind.Array||raw.GetArrayLength()>1000)throw new Exception("一次最多同步1000条鱼糕提醒");
            var rows=new List<FishRow>();var seen=new HashSet<string>();
            foreach(var e in raw.EnumerateArray()){
                string Text(string name,int max){var v=e.GetProperty(name).GetString()??"";if(v.Length>max||v.Any(char.IsControl))throw new Exception("鱼糕提醒内容无效");return v;}
                var r=new FishRow{key=Text("key",140),title=Text("title",120),body=Text("body",500),at=e.GetProperty("at").GetInt64(),expires=e.GetProperty("expires").GetInt64()};
                if(r.key.Length==0||r.title.Length==0||r.at>now+30L*86400000||r.expires<r.at||r.expires>now+31L*86400000)throw new Exception("鱼糕提醒时间或内容无效");
                if(r.at>now&&seen.Add(r.key))rows.Add(r);
            }return rows.OrderBy(r=>r.at).ToList();
        }
        public FishPlan Get(string owner){if(!plans.TryGetValue(owner,out var p)){if(plans.Count>=32)throw new Exception("鱼糕提醒设备过多");plans[owner]=p=new();}return p;}
        public void Replace(string owner,JsonElement raw,bool enabled,int mode,long now){if(mode<0||mode>2)throw new Exception("鱼糕通知方式无效");var rows=Validate(raw,now);var p=Get(owner);p.sent=p.sent.Where(x=>x.Value>now-31L*86400000).ToDictionary(x=>x.Key,x=>x.Value);if(plans.Where(x=>x.Key!=owner).Sum(x=>x.Value.rows.Count)+rows.Count>4000)throw new Exception("服务端鱼糕计划已满，最多4000条");p.rows=rows.Where(r=>!p.sent.ContainsKey(r.key)).ToList();p.enabled=enabled;p.mode=mode;}
        public List<(string owner,FishRow row,int mode)> TakeDue(long now){
            var due=new List<(string,FishRow,int)>();
            foreach(var pair in plans){var p=pair.Value;if(!p.enabled)continue;foreach(var r in p.rows.Where(r=>r.at<=now).ToArray()){
                if(r.expires>now&&r.at>now-900000&&!p.sent.ContainsKey(r.key))due.Add((pair.Key,r,p.mode));
                p.sent[r.key]=now;p.rows.Remove(r);if(p.sent.Count>1000)p.sent=p.sent.OrderByDescending(x=>x.Value).Take(1000).ToDictionary(x=>x.Key,x=>x.Value);
            }}return due;
        }
    }



    // Windows ships SQLite; the bounded page cache belongs to SQLite, not a managed history list.
    internal sealed class DiskChatStore : IEnumerable<ChatEntry>, IDisposable
    {
        private IntPtr db;
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] filename,out IntPtr database,int flags,IntPtr vfs);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr database);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr database,byte[] sql,int bytes,out IntPtr statement,IntPtr tail);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement,int column);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement,int column);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_changes(IntPtr database);
        [System.Runtime.InteropServices.DllImport("winsqlite3",CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr database);
        private static byte[] Utf8(string value)=>Encoding.UTF8.GetBytes(value+"\0");
        private static string Quote(string value)=>"'"+(value??"").Replace("'","''").Replace("\0","")+"'";
        public DiskChatStore(string path,bool reset)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if(sqlite3_open_v2(Utf8(path),out db,6|0x10000,IntPtr.Zero)!=0)throw new IOException("无法打开聊天缓存");
            try {
                Exec("PRAGMA journal_mode=WAL");Exec("PRAGMA synchronous=NORMAL");Exec("PRAGMA cache_size=-1024");Exec("PRAGMA busy_timeout=3000");
                Exec("CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY,channel TEXT,historyKey TEXT,stamp INTEGER,kind INTEGER,payload TEXT)");
                Exec("CREATE INDEX IF NOT EXISTS pending ON messages(channel,kind,historyKey,stamp)");Exec("CREATE INDEX IF NOT EXISTS channels ON messages(channel)");Exec("CREATE INDEX IF NOT EXISTS history_key ON messages(historyKey)");
                Exec("CREATE TABLE IF NOT EXISTS seen(key TEXT PRIMARY KEY)");
                Exec("CREATE TABLE IF NOT EXISTS source_proofs(record_key TEXT PRIMARY KEY,origin TEXT NOT NULL)");
                var columns=Prepare("PRAGMA table_info(messages)");bool origin=false;try{while(sqlite3_step(columns)==100)if(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(sqlite3_column_text(columns,1))=="origin")origin=true;}finally{sqlite3_finalize(columns);}
                if(!origin)Exec("ALTER TABLE messages ADD COLUMN origin TEXT NOT NULL DEFAULT ''");
                Exec("CREATE INDEX IF NOT EXISTS by_origin ON messages(origin,id)");
                Exec("CREATE TABLE IF NOT EXISTS notices(id INTEGER PRIMARY KEY AUTOINCREMENT,stamp INTEGER,payload TEXT)");
                if(reset)Clear();
            } catch {Dispose();throw;}
        }
        private IntPtr Prepare(string sql){if(sqlite3_prepare_v2(db,Utf8(sql),-1,out var stmt,IntPtr.Zero)!=0)throw new IOException(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));return stmt;}
        private void CheckStep(int code){if(code!=100&&code!=101)throw new IOException(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));}
        private void Exec(string sql){var stmt=Prepare(sql);try{CheckStep(sqlite3_step(stmt));}finally{sqlite3_finalize(stmt);}}
        private long Scalar(string sql){var stmt=Prepare(sql);try{var code=sqlite3_step(stmt);CheckStep(code);return code==100?sqlite3_column_int64(stmt,0):0;}finally{sqlite3_finalize(stmt);}}
        private IEnumerable<ChatEntry> Read(string sql){var stmt=Prepare(sql);try{while(true){var code=sqlite3_step(stmt);CheckStep(code);if(code==101)break;yield return JsonSerializer.Deserialize<ChatEntry>(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(sqlite3_column_text(stmt,0)));}}finally{sqlite3_finalize(stmt);}}
        public int Count=>(int)Scalar("SELECT count(*) FROM messages");
        public bool HasAfter(long id)=>Scalar("SELECT EXISTS(SELECT 1 FROM messages WHERE origin<>'' AND id>"+id+")")!=0;
        public long CountAfter(long id)=>Scalar("SELECT count(*) FROM messages WHERE id>"+id);
        public long LastTrustedId=>Scalar("SELECT coalesce(max(id),0) FROM messages WHERE origin<>''");
        public long LastId=>Scalar("SELECT coalesce(max(id),0) FROM messages");
        public ChatEntry this[int index]=>Read("SELECT payload FROM messages ORDER BY id LIMIT 1 OFFSET "+index).First();
        public void Add(ChatEntry entry)=>Put(entry);
        public void Notify(ChatEntry entry){Exec("INSERT INTO notices(stamp,payload) VALUES("+DateTimeOffset.UtcNow.ToUnixTimeSeconds()+","+Quote(JsonSerializer.Serialize(entry))+")");entry.id=Scalar("SELECT last_insert_rowid()");Exec("UPDATE notices SET payload="+Quote(JsonSerializer.Serialize(entry))+" WHERE id="+entry.id);Exec("DELETE FROM notices WHERE id<"+(entry.id-128)+" OR stamp<"+(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-1800));}
        public ChatEntry[] NoticesAfter(long id)=>Read("SELECT payload FROM notices WHERE id>"+id+" ORDER BY id LIMIT 128").ToArray();
        public void Delete(long id)=>Exec("DELETE FROM messages WHERE id="+id);
        public ChatEntry ByKey(string key)=>Read("SELECT payload FROM messages WHERE historyKey="+Quote(key)+" LIMIT 1").FirstOrDefault();
        public ChatEntry ByNativeKey(string key)=>Read("SELECT payload FROM messages WHERE historyKey="+Quote(key)+" OR historyKey LIKE "+Quote(key+":%")+" ORDER BY id LIMIT 1").FirstOrDefault();
        public IEnumerable<ChatEntry> Cached(string channel,ushort kind,DateTimeOffset when)=>Read("SELECT payload FROM messages WHERE channel="+Quote(channel)+" AND kind="+kind+" AND historyKey<>'' AND stamp BETWEEN "+(when.UtcTicks-TimeSpan.FromSeconds(3).Ticks)+" AND "+(when.UtcTicks+TimeSpan.FromSeconds(3).Ticks)+" ORDER BY id");

        public void Put(ChatEntry entry){long stamp=DateTimeOffset.TryParse(entry.time,out var when)?when.UtcTicks:0;Exec("INSERT OR REPLACE INTO messages(id,channel,historyKey,stamp,kind,payload,origin) VALUES("+entry.id+","+Quote(entry.channel)+","+Quote(entry.historyKey)+","+stamp+","+entry.kind+","+Quote(JsonSerializer.Serialize(entry))+","+Quote(entry.recordSource)+")");}
        public bool MarkSeen(string key){Exec("INSERT OR IGNORE INTO seen VALUES("+Quote(key)+")");return sqlite3_changes(db)>0;}
        public void Clear(){Exec("DELETE FROM messages");Exec("DELETE FROM seen");Exec("DELETE FROM notices");Exec("DELETE FROM source_proofs");Exec("PRAGMA wal_checkpoint(TRUNCATE)");Exec("VACUUM");Exec("PRAGMA wal_checkpoint(TRUNCATE)");}
        public void Proof(string key,string origin)=>Exec("INSERT OR REPLACE INTO source_proofs(record_key,origin) VALUES("+Quote(key)+","+Quote(origin)+")");
        public string Origin(string key){var stmt=Prepare("SELECT origin FROM source_proofs WHERE record_key="+Quote(key));try{int code=sqlite3_step(stmt);CheckStep(code);return code==100?System.Runtime.InteropServices.Marshal.PtrToStringUTF8(sqlite3_column_text(stmt,0)):"unknown";}finally{sqlite3_finalize(stmt);}}
        public ChatEntry[] After(long id,int limit)=>Read("SELECT payload FROM messages WHERE origin<>'' AND id>"+id+" ORDER BY id LIMIT "+limit).ToArray();
        public ChatEntry[] Recent(int limit)=>Read("SELECT payload FROM messages WHERE origin<>'' ORDER BY id DESC LIMIT "+limit).Reverse().ToArray();
        public IEnumerable<ChatEntry> Pending(string channel,ushort kind,DateTimeOffset when)=>Read("SELECT payload FROM messages WHERE channel="+Quote(channel)+" AND kind="+kind+" AND historyKey='' AND stamp BETWEEN "+(when.UtcTicks-TimeSpan.FromSeconds(3).Ticks)+" AND "+(when.UtcTicks+TimeSpan.FromSeconds(3).Ticks)+" ORDER BY id");
        public ChatEntry ById(long id)=>Read("SELECT payload FROM messages WHERE id="+id).FirstOrDefault();
        public string[] Channels(){var result=new List<string>();var stmt=Prepare("SELECT DISTINCT channel FROM messages WHERE origin<>''");try{while(sqlite3_step(stmt)==100)result.Add(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(sqlite3_column_text(stmt,0)));}finally{sqlite3_finalize(stmt);}return result.ToArray();}
        public IEnumerator<ChatEntry> GetEnumerator()=>Read("SELECT payload FROM messages ORDER BY id").GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        public void Dispose(){if(db!=IntPtr.Zero){sqlite3_close_v2(db);db=IntPtr.Zero;}}
    }

    public sealed class PhoneTab
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string source { get; set; } = "";
        public string filterSignature { get; set; } = "";
        public ushort[] kinds { get; set; } = Array.Empty<ushort>();
        public bool world { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string visible { get; set; } = "";
        public string recordSource { get; set; } = "native";
        public bool nativeWindow { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public Func<ushort,byte,byte,bool> filter { get; set; }
    }

    public sealed class RecruitmentLink
    {public string listingId{get;set;}="";public string kind{get;set;}="listing";public string source{get;set;}="game";public string title{get;set;}="队伍招募";public string comment{get;set;}="";public string postId{get;set;}="";}

    public sealed class DirectListing
    {
        public ulong id;public uint category;public ushort duty,dutyType,itemLevel,secondsRemaining;public ushort homeWorld,currentWorld;public byte area,totalSlots,filledSlots;public string leader="",comment="";public byte[] leaderData=Array.Empty<byte>(),commentData=Array.Empty<byte>();public byte[] jobs=Array.Empty<byte>();public ulong[] slotFlags=Array.Empty<ulong>();public long expires;
    }

    public sealed class DirectBoardCache
    {
        private readonly object sync=new();private Dictionary<ulong,DirectListing> staged=new(),current=new();private bool receiving,received;private long lastPacket,requestedAt,updatedAt;
        public void Begin(long now){lock(sync){receiving=true;received=false;staged.Clear();requestedAt=now;lastPacket=0;}}
        public void Packet(IEnumerable<DirectListing> rows,long now){lock(sync){if(!receiving||now-requestedAt>6000)return;foreach(var row in rows)if(row.id!=0&&staged.Count<500)staged[row.id]=row;received=true;lastPacket=now;}}
        public bool Complete(long now){lock(sync){if(!receiving||!received||now-lastPacket<350)return false;current=staged;staged=new();receiving=false;updatedAt=now;return true;}}
        public void Cancel(){lock(sync){receiving=false;staged.Clear();}}
        public void Clear(){lock(sync){receiving=false;received=false;staged.Clear();current.Clear();updatedAt=0;}}
        public DirectListing[] Snapshot(long now){lock(sync)return current.Values.Where(r=>r.expires>now).ToArray();}
        public DirectListing Get(ulong id,long now){lock(sync)return current.TryGetValue(id,out var row)&&row.expires>now?row:null;}
        public bool IsReceiving{get{lock(sync)return receiving;}}
        public long UpdatedAt{get{lock(sync)return updatedAt;}}
    }

}
