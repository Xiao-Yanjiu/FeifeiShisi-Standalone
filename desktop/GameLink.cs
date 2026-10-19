using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Trial;

internal sealed record GameProcess(int Pid, long Started, string Path, string Title, string Character, string ContentId, ushort World, string Error = "")
{
    public override string ToString() => $"{(Character.Length > 0 ? Character + " @ " + GameNames.World(World.ToString()) : Error.Length > 0 ? "无法读取角色" : "未登录角色")} · PID {Pid} · {Title}";
}
internal sealed record ChatLine(long id, DateTimeOffset time, string channel, string sender, string text, string target = "", bool outgoing = false,ushort kind=0,byte sourceKind=0,byte targetKind=0,bool notify=false);
internal sealed record GameIdentity(string name, string world, string contentId, int pid, bool online, byte job = 0, byte level = 0, uint hp = 0, uint maxHp = 0);
internal sealed record RecruitRow(string id, uint category, ushort duty, ushort dutyType, string leader, string comment, ushort itemLevel, ushort homeWorld, ushort currentWorld, byte totalSlots, byte filledSlots, byte[] jobs, DateTimeOffset expires)
{
    public string homeWorldName => GameNames.World(homeWorld.ToString());
}

internal sealed class GameProfile
{
    public string ImageSha256 { get; set; } = "";
    public uint Timestamp { get; set; }
    public uint ImageSize { get; set; }
    public Dictionary<string, uint> Addresses { get; set; } = new();
    public string PrintPrologue { get; set; } = "";
    public string ListingPrologue { get; set; } = "";
    public static GameProfile Load()
    {
        var assembly = typeof(GameProfile).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("game-profile.json")) ?? throw new InvalidDataException("此构建缺少已验证的游戏版本配置");
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<GameProfile>(stream) ?? throw new InvalidDataException("游戏版本配置无效");
    }
}

internal sealed class GameLink : IDisposable
{
    readonly object gate = new();
    readonly Queue<ChatLine> history = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource<string>> replies = new();
    readonly ConcurrentDictionary<long, RecruitWaiter> recruitment = new();
    readonly Dictionary<int, RecruitRow[]> recruitmentCache = new();
    long lastRecruitment;
    public byte[] NativeFilters {get;private set;}=[];long filterAt;int filterReading;
    OriginalDesktop.PhoneChatCore.DiskChatStore? disk;string diskCid="";
    readonly SemaphoreSlim sendGate = new(1, 1);
    CancellationTokenSource? session;
    NamedPipeServerStream? pipe;
    long sequence, commandId;
    long generation;
    long sourceStarted;
    string error = "未连接游戏";
    GameIdentity identity = new("", "", "", 0, false);
    public GameIdentity Identity { get { lock (gate) return identity; } }
    public string Status { get { lock (gate) return error; } }
    public long LastId {get{lock(gate)return sequence;}}
    public event Action<ChatLine>? NativeChat;
    public ChatLine[] Messages(long after) {lock(gate){var rows=new List<ChatLine>();int bytes=0;foreach(var entry in history.Where(m=>m.id>after)){int size=JsonSerializer.SerializeToUtf8Bytes(entry).Length;if(rows.Count>0&&bytes+size>350*1024)break;rows.Add(entry);bytes+=size;if(rows.Count>=300)break;}return rows.ToArray();}}
    public bool HasMore(long after){lock(gate)return history.Any(m=>m.id>after);}
    public static readonly Dictionary<int,string> ChannelNames = new(){
        [10]="说话",[11]="喊话",[12]="悄悄话",[13]="悄悄话",[14]="小队",[15]="团队",[16]="通讯贝1",[17]="通讯贝2",[18]="通讯贝3",[19]="通讯贝4",[20]="通讯贝5",[21]="通讯贝6",[22]="通讯贝7",[23]="通讯贝8",[24]="部队",[27]="新人",[28]="表情",[29]="表情",[30]="呼喊",[32]="小队",[37]="跨服贝1",[56]="默语",[101]="跨服贝2",[102]="跨服贝3",[103]="跨服贝4",[104]="跨服贝5",[105]="跨服贝6",[106]="跨服贝7",[107]="跨服贝8"
    };
    public static readonly Dictionary<string,string> Commands = new(){
        ["说话"]="/s",["喊话"]="/sh",["悄悄话"]="/tell",["小队"]="/p",["团队"]="/a",["部队"]="/fc",["新人"]="/b",["呼喊"]="/y",
        ["通讯贝1"]="/l1",["通讯贝2"]="/l2",["通讯贝3"]="/l3",["通讯贝4"]="/l4",["通讯贝5"]="/l5",["通讯贝6"]="/l6",["通讯贝7"]="/l7",["通讯贝8"]="/l8",
        ["跨服贝1"]="/cwl1",["跨服贝2"]="/cwl2",["跨服贝3"]="/cwl3",["跨服贝4"]="/cwl4",["跨服贝5"]="/cwl5",["跨服贝6"]="/cwl6",["跨服贝7"]="/cwl7",["跨服贝8"]="/cwl8"
    };
    public async Task ConnectAsync(GameProcess game)
    {
        if (pipe?.IsConnected == true && Identity.pid == game.Pid && sourceStarted == game.Started) return;
        Disconnect();
        var current = Interlocked.Increment(ref generation);
        var cts = new CancellationTokenSource(); session = cts;
        var token = cts.Token;
        lock (gate) { error = "正在核对游戏版本…"; history.Clear(); identity = new(game.Character, game.World.ToString(), game.ContentId, game.Pid, false); }
        try
        {
            var profile = GameProfile.Load();
            await Task.Run(() => NativeInjection.Validate(game, profile), token);
            string pipeName = $"FF14Chat.Trial.{game.Pid}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
            var local = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe = local;
            var connected = local.WaitForConnectionAsync(token);
            await Task.Run(() => NativeInjection.Start(game, profile, pipeName), token);
            await connected.WaitAsync(TimeSpan.FromSeconds(12), token);
            if (!GetNamedPipeClientProcessId(local.SafePipeHandle, out uint nativePid) || nativePid != game.Pid) throw new IOException("连接组件进程身份不匹配");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var hello = JsonDocument.Parse(await ReadPacket(local, timeout.Token));
            if (hello.RootElement.GetProperty("type").GetString() != "hello" || hello.RootElement.GetProperty("pid").GetInt32() != game.Pid || hello.RootElement.GetProperty("protocol").GetInt32() != 1) throw new IOException("游戏连接握手不匹配");
            if (hello.RootElement.TryGetProperty("error", out var failure) && failure.GetString() is { Length: >0 } message) throw new IOException(message);
            lock (gate) error = "已连接游戏";
            sourceStarted = game.Started;
            _ = ReadLoop(local, current, token);
        }
        catch (Exception e)
        {
            if (current == generation) { lock (gate) error = e is OperationCanceledException ? "连接已取消" : e.Message; pipe?.Dispose(); pipe = null; }
            Log.Write(e); throw;
        }
    }
    static async Task<byte[]> ReadPacket(Stream stream, CancellationToken token)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int length = BitConverter.ToInt32(size); if (length is <2 or >65536) throw new IOException("游戏连接帧长度无效");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token); return body;
    }
    async Task ReadLoop(Stream stream, long current, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var doc = JsonDocument.Parse(await ReadPacket(stream, deadline.Token)); if(current!=generation)break;var r = doc.RootElement;
                switch (r.GetProperty("type").GetString())
                {
                    case "state":
                        var next = new GameIdentity(r.GetProperty("name").GetString() ?? "", GameNames.World(r.GetProperty("world").GetString() ?? ""), r.GetProperty("cid").GetString() ?? "", r.GetProperty("pid").GetInt32(), r.GetProperty("online").GetBoolean(), r.GetProperty("job").GetByte(), r.GetProperty("level").GetByte(), r.GetProperty("hp").GetUInt32(), r.GetProperty("maxHp").GetUInt32());
                        lock (gate) { if (identity.contentId != next.contentId || (next.online&&diskCid!=next.contentId)) { history.Clear(); recruitmentCache.Clear();disk?.Dispose();disk=null;diskCid="";if(next.online&&ulong.TryParse(next.contentId,out _)){diskCid=next.contentId;disk=new(Path.Combine(Log.Root,"chat",diskCid+".db"),false);foreach(var saved in disk.Recent(5000)){history.Enqueue(new(saved.id,DateTimeOffset.Parse(saved.time),saved.channel,saved.sender,saved.text,saved.target,saved.outgoing,saved.kind,saved.sourceKind,saved.targetKind,saved.notify));sequence=Math.Max(sequence,saved.id);}} } identity = next; error = next.online ? "已连接游戏" : "等待角色登录"; }
                        if(next.online&&Environment.TickCount64-filterAt>5000&&Interlocked.CompareExchange(ref filterReading,1,0)==0){filterAt=Environment.TickCount64;_=Task.Run(async()=>{try{var encoded=await NativeAction("filters",next.contentId,token,5);var bytes=Convert.FromBase64String(encoded);if(Identity.contentId==next.contentId)NativeFilters=bytes;}catch(Exception e){Log.Write(e);}finally{Interlocked.Exchange(ref filterReading,0);}});}break;
                    case "chat":
                        int rawKind=r.GetProperty("kind").GetInt32();int kind=rawKind&127;byte sourceKind=(byte)((rawKind>>11)&15),targetKind=(byte)((rawKind>>7)&15);
                        if (!ChannelNames.TryGetValue(kind, out var channel)) channel="系统 / "+GameCatalog.ChatKind(kind);
                        var senderBytes=Convert.FromBase64String(r.GetProperty("sender").GetString()??"");string sender=NativePlayerLink.Target(senderBytes);if(sender.Length==0)sender=SeStringText(senderBytes);string text=SeStringText(Convert.FromBase64String(r.GetProperty("body").GetString()??""));
                        if (text.Length == 0) break;
                        ChatLine entry;lock (gate) { entry=new(++sequence, DateTimeOffset.Now, channel, sender, text, kind is 12 or 13 ? sender : "", kind == 12,(ushort)kind,sourceKind,targetKind);if(disk!=null&&diskCid==identity.contentId)disk.Add(new(){id=entry.id,time=entry.time.ToString("O"),channel=entry.channel,sender=entry.sender,text=entry.text,target=entry.target,outgoing=entry.outgoing,kind=entry.kind,sourceKind=entry.sourceKind,targetKind=entry.targetKind,recordSource="native",systemOrigin="game"});history.Enqueue(entry); while (history.Count > 5000) history.Dequeue(); }try{NativeChat?.Invoke(entry);}catch(Exception e){Log.Write(e);}break;
                    case "ack":
                        long ackId = r.GetProperty("id").GetInt64(); string ackError = r.GetProperty("error").GetString() ?? "";
                        if (replies.TryRemove(ackId, out var completion)) completion.TrySetResult(NativeError(ackError));
                        if (recruitment.TryRemove(ackId, out var early)) early.Completion.TrySetException(new IOException(NativeError(ackError.Length == 0 ? "Recruitment request did not return listings" : ackError))); break;
                    case "recruit":
                        if (!recruitment.TryGetValue(r.GetProperty("id").GetInt64(), out var pending)) break;
                        string recruitError = r.GetProperty("error").GetString() ?? "";
                        if (recruitError.Length > 0) { pending.Completion.TrySetException(new IOException(NativeError(recruitError))); break; }
                        int count = r.GetProperty("count").GetInt32(); var raw = Convert.FromBase64String(r.GetProperty("data").GetString() ?? "");
                        if (count is <0 or >16 || raw.Length != count * 400 || pending.Rows.Count + count > 256) throw new IOException("原生招募数据长度不匹配");
                        for (int i = 0; i < count; i++) { var recruitEntry = ParseRecruitRow(raw.AsSpan(i * 400, 400)); if (recruitEntry != null) pending.Rows.Add(recruitEntry); }
                        if (r.GetProperty("done").GetBoolean()) pending.Completion.TrySetResult(pending.Rows.ToArray()); break;
                    case "error": throw new IOException(r.GetProperty("error").GetString());
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (current == generation) { lock (gate) { error = token.IsCancellationRequested ? "未连接游戏" : "游戏连接中断：" + e.Message; identity = identity with { online = false }; } }
            if (current == generation)
            {
                foreach (var item in replies) if (replies.TryRemove(item.Key, out var value)) value.TrySetException(new IOException("游戏连接已断开"));
                foreach (var item in recruitment) if (recruitment.TryRemove(item.Key, out var value)) value.Completion.TrySetException(new IOException("游戏连接已断开"));
            }
        }
        finally
        {
            stream.Dispose();
            if (current == generation && ReferenceEquals(pipe, stream)) pipe = null;
        }
    }
    static string NativeError(string value)
    {
        if (value.StartsWith("Character changed")) return "角色已切换，操作已取消，请重新确认当前角色";
        if (value.StartsWith("No recruitment response")) return "没有收到游戏招募返回，已保留上次结果，请稍后刷新";
        if (value.StartsWith("Recruitment selection changed")) return "游戏中的招募分类已切换，本次手机刷新已取消";
        if (value.StartsWith("Recruitment batch was incomplete")) return "招募数据不完整，已保留上次结果";
        if (value.StartsWith("Chat request expired")) return "游戏未及时处理聊天，已取消过期请求";
        return value;
    }
    internal static RecruitRow? ParseRecruitRow(ReadOnlySpan<byte> data)
    {
        if (data.Length != 400) throw new InvalidDataException("招募条目长度无效");
        ulong id = BitConverter.ToUInt64(data); ushort lifetime = BitConverter.ToUInt16(data[68..]);
        if (id == 0 || lifetime == 0) return null;
        if (lifetime > 7200 || data[83] > 48 || data[84] > data[83]) throw new InvalidDataException("招募条目范围无效");
        return new(id.ToString(), BitConverter.ToUInt32(data[28..]), BitConverter.ToUInt16(data[32..]), BitConverter.ToUInt16(data[34..]), SeStringText(data.Slice(168, 32).ToArray()), SeStringText(data.Slice(200, 192).ToArray()), BitConverter.ToUInt16(data[76..]), BitConverter.ToUInt16(data[78..]), BitConverter.ToUInt16(data[80..]), data[83], data[84], data.Slice(160, 8).ToArray(), DateTimeOffset.UtcNow.AddSeconds(lifetime));
    }
    public async Task<object> Recruitment(int area, bool refresh, CancellationToken token)
    {
        if (area is <0 or >2) throw new InvalidDataException("招募地区无效");
        var who = Identity; if (!who.online) throw new IOException("请先连接已登录角色");
        RecruitRow[]? old;
        lock (gate) recruitmentCache.TryGetValue(area, out old);
        if (!refresh && old != null) return new { ok = true, area, rows = old.Where(x => x.expires > DateTimeOffset.UtcNow).ToArray(), warning = "" };
        await sendGate.WaitAsync(token);
        try
        {
            if (who.contentId != Identity.contentId) throw new IOException("角色已切换，刷新已取消");
            long remaining = 6000 - (Environment.TickCount64 - lastRecruitment); if (remaining > 0) throw new IOException($"请在 {(remaining + 999) / 1000} 秒后再刷新游戏招募");
            long id = Interlocked.Increment(ref commandId); var wait = new RecruitWaiter(); recruitment[id] = wait;
            try
            {
                var bytes = new byte[24]; BitConverter.TryWriteBytes(bytes.AsSpan(), 2); BitConverter.TryWriteBytes(bytes.AsSpan(4), id); BitConverter.TryWriteBytes(bytes.AsSpan(12), ulong.Parse(who.contentId)); BitConverter.TryWriteBytes(bytes.AsSpan(20), area);
                var p = pipe ?? throw new IOException("游戏连接已断开"); await p.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await p.WriteAsync(bytes, token); await p.FlushAsync(token); lastRecruitment = Environment.TickCount64;
                var rows = await wait.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                if (who.contentId != Identity.contentId) throw new IOException("角色已切换，刷新结果已丢弃");
                lock (gate) recruitmentCache[area] = rows;
                return new { ok = true, area, rows, warning = "" };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (old != null && who.contentId == Identity.contentId) return new { ok = true, area, rows = old.Where(x => x.expires > DateTimeOffset.UtcNow).ToArray(), warning = e.Message };
                throw;
            }
            finally { recruitment.TryRemove(id, out _); }
        }
        finally { sendGate.Release(); }
    }
    sealed class RecruitWaiter
    {
        public readonly List<RecruitRow> Rows = [];
        public readonly TaskCompletionSource<RecruitRow[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal static string SeStringText(byte[] bytes)
    {
        // Skip complete SeString payloads rather than interpreting their binary bytes as UTF-8.
        using var clean = new MemoryStream();
        for (int i = 0; i < bytes.Length;)
        {
            byte b = bytes[i++];
            if (b == 0) break;
            if (b != 2) { if (b >= 32 || b is 10 or 9) clean.WriteByte(b); continue; }
            if (i + 1 >= bytes.Length) break; i++; byte prefix = bytes[i++]; uint length;
            if (prefix < 0xD0) { if (prefix == 0) break; length = (uint)(prefix - 1); }
            else { int mask = (prefix + 1) & 15; length = 0; bool invalid = false; for (int shift = 24; shift >= 0; shift -= 8) if ((mask & (1 << (shift / 8))) != 0) { if (i >= bytes.Length) { invalid = true; break; } length |= (uint)bytes[i++] << shift; } if (invalid) break; }
            if (length > bytes.Length - i || i + length >= bytes.Length || bytes[i + (int)length] != 3) break;
            i += (int)length + 1;
        }
        return new UTF8Encoding(false, false).GetString(clean.ToArray());
    }
    public async Task SendChat(string channel, string target, string text, string expectedCid, CancellationToken cancellation)
    {
        var who = Identity;
        if (!who.online || who.contentId != expectedCid) throw new InvalidOperationException("角色已切换，请重新确认当前角色");
        if (!Commands.TryGetValue(channel, out var command)) throw new InvalidDataException("不支持的游戏频道");
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.StartsWith('/')) throw new InvalidDataException("请输入普通聊天文字，不能包含控制符或宏指令");
        if (channel == "悄悄话") { if (target.Length is <2 or >80 || target.Any(c => char.IsControl(c) || c is '/' or '"')) throw new InvalidDataException("悄悄话对象无效"); command += " " + target; }
        string line = command + " " + text;
        var utf8 = Encoding.UTF8.GetBytes(line); if (utf8.Length > 500) throw new InvalidDataException("聊天内容过长");
        long id = Interlocked.Increment(ref commandId); var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        replies[id] = completion;
        try
        {
            var body = new byte[24 + utf8.Length]; BitConverter.TryWriteBytes(body.AsSpan(), 1); BitConverter.TryWriteBytes(body.AsSpan(4), id); BitConverter.TryWriteBytes(body.AsSpan(12), ulong.Parse(expectedCid)); BitConverter.TryWriteBytes(body.AsSpan(20), utf8.Length); utf8.CopyTo(body, 24);
            await sendGate.WaitAsync(cancellation);
            try
            {
                if (Identity.contentId != expectedCid) throw new IOException("角色已切换，未发送聊天");
                var p = pipe ?? throw new IOException("未连接游戏"); await p.WriteAsync(BitConverter.GetBytes(body.Length), cancellation); await p.WriteAsync(body, cancellation); await p.FlushAsync(cancellation);
                string failure = await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellation);
                if (failure.Length > 0) throw new IOException(failure);
            }
            finally { sendGate.Release(); }
        }
        finally { replies.TryRemove(id, out _); }
    }
    public async Task<string> NativeAction(string text,string expectedCid,CancellationToken token,int type=4)
    {
        var who=Identity;if(!who.online||who.contentId!=expectedCid)throw new IOException("角色已切换，操作取消");
        var utf8=Encoding.UTF8.GetBytes(text);if(utf8.Length is <1 or >500||text.Any(char.IsControl)||type is not (3 or 4 or 5))throw new InvalidDataException("游戏操作参数无效");
        await sendGate.WaitAsync(token);long id=Interlocked.Increment(ref commandId);var done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);replies[id]=done;
        try{if(Identity.contentId!=expectedCid)throw new IOException("角色已切换");var body=new byte[24+utf8.Length];BitConverter.TryWriteBytes(body.AsSpan(),type);BitConverter.TryWriteBytes(body.AsSpan(4),id);BitConverter.TryWriteBytes(body.AsSpan(12),ulong.Parse(expectedCid));BitConverter.TryWriteBytes(body.AsSpan(20),utf8.Length);utf8.CopyTo(body,24);var p=pipe??throw new IOException("游戏连接已断开");await p.WriteAsync(BitConverter.GetBytes(body.Length),token);await p.WriteAsync(body,token);await p.FlushAsync(token);var error=await done.Task.WaitAsync(TimeSpan.FromSeconds(4),token);if(type==5&&error.StartsWith("filters:"))return error[8..];if(error.Length>0)throw new IOException(error);return "";}finally{replies.TryRemove(id,out _);sendGate.Release();}
    }
    public void Reminder(string sender,string text){lock(gate){if(!identity.online)return;var entry=new ChatLine(++sequence,DateTimeOffset.Now,"提醒",sender,text,notify:true);history.Enqueue(entry);while(history.Count>5000)history.Dequeue();disk?.Add(new(){id=entry.id,channel=entry.channel,sender=sender,text=text,time=entry.time.ToString("O"),notify=true,recordSource="native",systemOrigin="game"});}}
    public void Disconnect()
    {
        Interlocked.Increment(ref generation); sourceStarted = 0;NativeFilters=[];filterAt=0; session?.Cancel(); pipe?.Dispose(); pipe = null; session?.Dispose(); session = null;
        lock (gate) { identity = new("", "", "", 0, false); error = "未连接游戏"; }
    }
    public void Dispose(){Disconnect();lock(gate){disk?.Dispose();disk=null;}}
    [DllImport("kernel32", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}

