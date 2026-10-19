using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Trial;
internal sealed class PhoneServer(AppState app) : IDisposable
{
    readonly RecruitmentAdapter recruit=new(app.Game);
    readonly PartyAdapter partyAdapter=new(app.Game);
    readonly SemaphoreSlim slots = new(16);
    readonly object gate = new();
    readonly Dictionary<string, (long Start, int Count)> attempts = new();
    readonly Dictionary<string, Task<object>> sends = new();
    TcpListener? listener; CancellationTokenSource? cancellation;
    X509Certificate2? certificate;
    readonly string runId = Guid.NewGuid().ToString("N");
    long lastTlsError;
    readonly ConcurrentDictionary<string,long> polls=new();
    public bool RecentPoll(string owner)=>polls.TryGetValue(owner,out var time)&&Environment.TickCount64-time<18000;
    public bool AnyDirectReceiver=>polls.Values.Any(time=>Environment.TickCount64-time<18000);
    public string Fingerprint { get; private set; } = "";
    public bool Running => listener != null;
    public void Start()
    {
        if (Running) return;
        certificate = LoadCertificate(); Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        if (app.Settings.Port is <1024 or >65535) throw new InvalidOperationException("端口应在 1024–65535 范围内");
        var next = new TcpListener(IPAddress.Any, app.Settings.Port);
        try { next.Start(16); } catch { next.Stop(); certificate.Dispose(); certificate = null; throw; }
        listener = next; cancellation = new(); _ = Accept(next, certificate, cancellation.Token);
    }
    async Task Accept(TcpListener current, X509Certificate2 cert, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await current.AcceptTcpClientAsync(token); } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { break; }
            if (!await slots.WaitAsync(0, token)) { client.Dispose(); continue; }
            _ = Serve(client, cert, token);
        }
    }
    async Task Serve(TcpClient client, X509Certificate2 cert, CancellationToken stop)
    {
        using (client) using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(18)); var token = deadline.Token;
            using var tls = new SslStream(client.GetStream(), false);
            try
            {
                client.NoDelay = true;
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token);
                var request = await Request.Read(tls, token);
                var uri = new Uri("https://localhost" + request.Target);
                string path = uri.AbsolutePath;
                if (path == "/pair-info" && request.Method == "GET") { await Reply(tls, 200, new { manualAuthorization = true, version = "0.1.2-trial", edition = "standalone" }, token); return; }
                if (path is "/pair-request" or "/pair-status" && request.Method == "POST")
                {
                    LimitPairing(((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? "unknown");
                    using var document = JsonDocument.Parse(request.Body);
                    await Reply(tls, 200, app.Pairing.Request(document.RootElement, path == "/pair-request", Fingerprint), token); return;
                }
                if (!app.Pairing.Authorized(request.Authorization)) { await Reply(tls, 401, new { error = "设备未授权或授权已撤销，请重新连接" }, token); return; }
                string? page=path switch{"/send"=>"chat","/party"=>"party","/recruit" or "/recruit-board"=>"recruit","/character" or "/item"=>"character","/fishing-reminders"=>"fishing",_=>path.StartsWith("/stream")?"stream":null};
                if(page!=null&&app.Settings.DisabledPages.Contains(page)){await Reply(tls,403,new{error="服务端已停用该页面"},token);return;}
                using var data = request.Body.Length > 0 ? JsonDocument.Parse(request.Body) : JsonDocument.Parse("{}");
                var r = data.RootElement;
                if (path == "/poll" && request.Method == "GET")
                {
                    polls[FishingQueue.Owner(request.Authorization)]=Environment.TickCount64;var query = System.Web.HttpUtility.ParseQueryString(uri.Query); long.TryParse(query["after"], out long after); long.TryParse(query["noticeAfter"], out long noticeAfter);
                    for (int i = 0; i < 18 && app.Game.Messages(after).Length == 0; i++) { await Task.Delay(400, token); if (!app.Pairing.Authorized(request.Authorization)) { await Reply(tls, 401, new { error = "设备授权已撤销" }, token); return; } }
                    await Reply(tls, 200, Snapshot(after, app.Fishing.Due(request.Authorization, noticeAfter),noticeAfter), token); return;
                }
                if (path == "/reload" && request.Method == "POST") { await Reply(tls, 200, new { ok = true }, token); return; }
                if (path == "/send" && request.Method == "POST")
                {
                    string id = r.GetProperty("requestId").GetString() ?? "";
                    if (!Guid.TryParse(id, out _)) throw new InvalidDataException("发送请求必须包含唯一编号");
                    string key = request.Authorization + ":" + id; Task<object> send;
                    lock (gate)
                    {
                        if (!sends.TryGetValue(key, out send!))
                        {
                            if (sends.Count >= 1000) throw new InvalidOperationException("发送记录已满，请重启试用版服务端后继续");
                            string channel = r.GetProperty("channel").GetString() ?? "", target = r.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "", text = r.GetProperty("text").GetString() ?? "", cid = r.GetProperty("contentId").GetString() ?? "";
                            send = Send(channel, target, text, cid, stop); sends[key] = send;
                        }
                    }
                    await Reply(tls, 200, await send.WaitAsync(token), token); return;
                }
                if (path == "/character" && request.Method == "POST") { await Reply(tls, 200, await Task.Run(()=>{using var reader=new CharacterReader(app.Game.Identity);return reader.Read(r.GetProperty("section").GetString()??"character");},token), token); return; }
                if (path == "/party" && request.Method == "POST")
                {
                    await Reply(tls,200,await partyAdapter.Action(r,request.Authorization,token),token);return;
                }
                if (path == "/recruit-board" && request.Method == "POST") { await Reply(tls, 200, await recruit.Board(r,request.Authorization,token), token); return; }
                if(path=="/recruit"&&request.Method=="POST"){await Reply(tls,200,await recruit.Detail(r,request.Authorization,token),token);return;}
                if(path=="/icons"&&request.Method=="POST"){await Reply(tls,200,GameAssets.Icons(r),token);return;}
                if(path=="/item"&&request.Method=="POST"){await Reply(tls,200,GameAssets.Item(r.GetProperty("itemId").GetInt32()),token);return;}
                if(path=="/display-assets"&&request.Method=="GET"){await Reply(tls,200,GameAssets.Display(System.Web.HttpUtility.ParseQueryString(uri.Query)["kind"]??""),token);return;}
                if (path == "/fishing-reminders" && request.Method == "POST") { await Reply(tls, 200, app.Fishing.Action(r, request.Authorization), token); return; }
                if (path == "/stream" && request.Method == "POST") { await Reply(tls, 200, await app.Capture.Action(r, request.Authorization, token), token); return; }
                if (path is "/stream-control" or "/stream-pointer" or "/stream-ime" or "/stream-text" && request.Method == "POST") {await Reply(tls,200,app.Capture.OriginalInput(path,r,request.Authorization),token);return;}
                if (path == "/stream-input" && request.Method == "POST") { await Reply(tls, 200, r.TryGetProperty("keys",out _)?app.Capture.OriginalInput(path,r,request.Authorization):app.Capture.Input(r, request.Authorization), token); return; }
                if (path == "/stream-video" && request.Method == "GET")
                {
                    string id = System.Web.HttpUtility.ParseQueryString(uri.Query)["session"] ?? "";
                    if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("画面会话无效");
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    await app.Capture.Video(tls, request.Authorization, id, () => app.Pairing.Authorized(request.Authorization), token); return;
                }
                await Reply(tls, 404, new { error = "独立版没有此接口" }, token);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e is AuthenticationException && Environment.TickCount64 - Interlocked.Read(ref lastTlsError) > 30000) { Interlocked.Exchange(ref lastTlsError, Environment.TickCount64); Log.Write(e.GetBaseException()); }
                if (e is not OperationCanceledException && e is not IOException && e is not AuthenticationException) Log.Write(e);
                try { if (tls.IsAuthenticated && !token.IsCancellationRequested) await Reply(tls, e is UnauthorizedAccessException ? 403 : 400, new { error = e is JsonException ? "请求格式无效" : e.Message }, token); } catch { }
            }
            finally { slots.Release(); }
        }
    }
    async Task<object> Send(string channel, string target, string text, string cid, CancellationToken token)
    {
        try { if(channel=="宏"||channel=="游戏宏"){if(!app.Settings.AllowCommands)throw new IOException("电脑未允许宏指令");if(channel=="游戏宏"){if(!int.TryParse(target,out int set)||set is <0 or >1||!int.TryParse(text,out int index)||index is <0 or >99)throw new InvalidDataException("游戏宏编号无效");await app.Game.NativeAction($"macro:{set}:{index}",cid,token);}else{if(!text.StartsWith('/'))throw new InvalidDataException("系统指令必须以 / 开头");await app.Game.NativeAction(text,cid,token,3);}}else await app.Game.SendChat(channel, target, text, cid, token); return new { ok = true, accepted = true, message = "已提交到游戏；发送结果以游戏聊天反馈为准" }; }
        catch (Exception e) { return new { ok = false, error = e.Message }; }
    }
    readonly object metadataGate=new();long metadataAt;string metadataCharacter="";object[] phoneMacros=[];string[] phoneTabNames=["通常","战斗","剧情","聊天4"];string metadataError="";
    object Snapshot(long after, FishNotice[] notices,long noticeAfter=0)
    {
        var who = app.Game.Identity;
        var nativeMessages=app.Game.Messages(after);
        lock(metadataGate){if(!who.online||metadataCharacter!=who.contentId){phoneMacros=[];metadataCharacter=who.contentId;metadataAt=0;}if(who.online&&Environment.TickCount64-metadataAt>3000){metadataAt=Environment.TickCount64;try{using var reader=new CharacterReader(who);phoneMacros=app.Settings.AllowCommands?reader.Macros():[];phoneTabNames=reader.TabNames();metadataError="";}catch(Exception e){metadataError=e.Message;}}if(!app.Settings.AllowCommands)phoneMacros=[];}

        var messages=nativeMessages.Select(m=>new{id=m.id,time=m.time,channel=m.channel,sender=m.sender,text=m.text,target=m.target,outgoing=m.outgoing,notify=m.notify||(app.Settings.ReceiveTell&&m.channel=="悄悄话"&&!m.outgoing),color=m.channel=="悄悄话"?"#f0a7d4":"#e5dfd5",recordSource="native",kind=m.kind,sourceKind=m.sourceKind,targetKind=m.targetKind,systemOrigin="game",parts=Array.Empty<object>(),senderParts=Array.Empty<object>(),tabs=NativeChatFilters.Match(app.Game.NativeFilters,m.kind,m.sourceKind,m.targetKind)});
        var fishNotices=notices.Select(n=>new{id=n.id,time=DateTimeOffset.UtcNow,channel="鱼糕",sender=n.title,text=n.body,target=n.key,notify=true,outgoing=false,externalPush=false});
        var channels=GameLink.Commands.Keys.Concat(new[]{"表情","默语"}).Concat(nativeMessages.Select(m=>m.channel)).Distinct().ToArray();
        return new { edition = "standalone", state = who.online?"已连接 · "+app.Game.Status:app.Game.Status, gameId = runId + ":" + who.pid + ":" + who.contentId, historyVersion = runId, gameAccount = new{name=who.name,world=who.world,job=who.job,level=who.level,loggedIn=who.online,online=who.online,contentId=who.contentId,pid=who.pid,hp=who.hp,maxHp=who.maxHp}, messages, notices=fishNotices.Cast<object>().Concat(app.Notices(noticeAfter).Cast<object>()).OrderBy(n=>JsonSerializer.SerializeToElement(n).GetProperty("id").GetInt64()), channels, tabs=phoneTabNames.Select((name,index)=>new{id="native:"+index,name,source="原生",recordSource="native",nativeWindow=true}).ToArray(),routes=app.Game.Messages(0).Select(m=>new{m.id,tabs=NativeChatFilters.Match(app.Game.NativeFilters,m.kind,m.sourceKind,m.targetKind)}).ToArray(),macros=phoneMacros,commands=app.Settings.AllowCommands?GameCatalog.Rows("commands").Where(c=>GameCatalog.Text(c,"name").StartsWith('/')).ToArray():Array.Empty<JsonElement>(),filterOptions=GameCatalog.Rows("filters").Where(r=>r.GetProperty("kind").GetInt32()>0&&r.GetProperty("category").GetInt32()>0&&GameCatalog.Text(r,"name").Length>0).Select(r=>new{id=$"f:{r.GetProperty("kind")}:{r.GetProperty("caster")}:{r.GetProperty("target")}",name=GameCatalog.Text(r,"name"),category=r.GetProperty("category").GetInt32(),kind=r.GetProperty("kind").GetInt32(),caster=r.GetProperty("caster").GetInt32(),target=r.GetProperty("target").GetInt32(),channel=GameLink.ChannelNames.GetValueOrDefault(r.GetProperty("kind").GetInt32())??"系统 / "+GameCatalog.ChatKind(r.GetProperty("kind").GetInt32())}).ToArray(),channelInfo=Array.Empty<object>(),metadataRevision=metadataAt,metadataError,allowCommands=app.Settings.AllowCommands,hasMore=app.Game.HasMore(nativeMessages.Length>0?nativeMessages[^1].id:after),pageNavigation = OriginalSettingsPanel.NormalizePages(app.Settings.PageOrder).Select(id => new { id, enabled = !app.Settings.DisabledPages.Contains(id) }), capabilities = new { chat = who.online, character = who.online, nativeBridge = true, party = who.online, recruit = who.online, stream = app.Capture.Available } };
    }
    void LimitPairing(string ip)
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            foreach (var key in attempts.Where(p => now - p.Value.Start > 60000).Select(p => p.Key).ToArray()) attempts.Remove(key);
            if (!attempts.TryGetValue(ip, out var value)) value = (now, 0);
            if (value.Count >= 180 || attempts.Count >= 256) throw new InvalidOperationException("授权请求过多，请稍后重试");
            attempts[ip] = (value.Start, value.Count + 1);
        }
    }
    static async Task Reply(Stream stream, int code, object value, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
    static X509Certificate2 LoadCertificate()
    {
        Directory.CreateDirectory(Log.Root); string path = Path.Combine(Log.Root, "certificate.bin");
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12(PrivateData.Unprotect(File.ReadAllBytes(path)), null, X509KeyStorageFlags.DefaultKeySet);
        using var rsa = RSA.Create(2048); var req = new CertificateRequest("CN=FF14ChatStandaloneTrial", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        var pfx = cert.Export(X509ContentType.Pfx); File.WriteAllBytes(path, PrivateData.Protect(pfx));
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
    }
    public void Dispose() { cancellation?.Cancel(); listener?.Stop(); listener = null; /* Certificates are retained until process exit so active TLS handshakes never see a disposed key. */ }

    internal sealed record Request(string Method, string Target, string Authorization, byte[] Body)
    {
        public static async Task<Request> Read(Stream stream, CancellationToken token)
        {
            using var head = new MemoryStream(); var one = new byte[1]; int suffix = 0;
            while (suffix != 4)
            {
                if (head.Length >= 8192) throw new InvalidDataException("请求头过长");
                await stream.ReadExactlyAsync(one, token); head.WriteByte(one[0]);
                suffix = one[0] == (suffix is 0 or 2 ? 13 : 10) ? suffix + 1 : one[0] == 13 ? 1 : 0;
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
            if (first.Length != 3 || first[0] is not ("GET" or "POST") || !first[1].StartsWith('/') || first[1].StartsWith("//") || first[1].Any(c => c is '\r' or '\n') || first[2] != "HTTP/1.1") throw new InvalidDataException("请求行无效");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1).Where(s => s.Length > 0)) { int i = line.IndexOf(':'); if (i <= 0 || !headers.TryAdd(line[..i], line[(i + 1)..].Trim())) throw new InvalidDataException("请求头重复或无效"); }
            if (headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("不支持分块请求");
            int maximum = first[1] == "/fishing-reminders" ? 1024 * 1024 : 8192;
            int length = 0; if (headers.TryGetValue("Content-Length", out var size) && (!int.TryParse(size, out length) || length < 0 || length > maximum)) throw new InvalidDataException("请求体过长或无效");
            if (first[0] == "GET" && length != 0) throw new InvalidDataException("GET 请求不能携带请求体");
            var body = new byte[length]; await stream.ReadExactlyAsync(body, token);
            return new(first[0], first[1], headers.GetValueOrDefault("Authorization", ""), body);
        }
    }
}

internal static class PrivateData
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob result);
    [DllImport("crypt32", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob result);
    [DllImport("kernel32")] static extern nint LocalFree(nint memory);
    public static byte[] Protect(byte[] value) => Transform(value, true);
    public static byte[] Unprotect(byte[] value) => Transform(value, false);
    static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length); Blob output;
            bool ok = protect ? CryptProtectData(ref input, 0, 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new IOException("无法使用当前 Windows 账号保护证书");
            try { var data = new byte[output.Size]; Marshal.Copy(output.Data, data, 0, data.Length); return data; } finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}

