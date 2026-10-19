using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trial;
internal static class TrialChecks
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAILED: " + name); count++; }
    static async Task Reject(string request, string name)
    {
        bool failed = false; try { using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request)); await PhoneServer.Request.Read(stream, CancellationToken.None); } catch (InvalidDataException) { failed = true; }
        Check(failed, name);
    }
    static JsonElement Json(object data) => JsonSerializer.SerializeToElement(data);
    public static async Task Main()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "work", "standalone-trial", "tests", "runtime", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("FF14_TRIAL_DATA_ROOT", root);
        Check(Log.Root == root, "test storage isolation");
        if(Environment.GetEnvironmentVariable("FF14_TRIAL_CAPTURE_CHECK") is string captureRoot&&captureRoot.Length>0){await RealCaptureChecks.Run(captureRoot);return;}
        byte[] header=[0,0,0,1,0x67,0x42,0,0,1,0x68,0x12], idr=[0,0,1,0x65,0x21];
        var boot=new H264Bootstrap();boot.Prepare(header);var complete=boot.Prepare(idr);
        Check(H264Bootstrap.Parameter(complete,7)!=null&&H264Bootstrap.Parameter(complete,8)!=null&&PhoneUdpVideoSession.IsKey(complete),"IDR recovery includes cached SPS and PPS");
        Check(boot.Prepare(complete).SequenceEqual(complete),"self contained keyframe remains unchanged");
        Check(H264Bootstrap.Parameter([0,0,0,1],7)==null,"truncated Annex B prefix rejected");
        if(Environment.GetEnvironmentVariable("FF14_TRIAL_UDP_FIXTURE") is string fixture&&fixture.Length>0){
            using var udp=new PhoneUdpVideoSession(19879,8000000,10,()=>{},()=>{},()=>{});
            File.WriteAllText(fixture,JsonSerializer.Serialize(new{port=udp.Port,key=udp.Secret}));
            var frame=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixture)!,"first-key.h264"));
            for(int i=0;i<120;i++){udp.Offer(frame);await Task.Delay(100);}
            Console.WriteLine("UDP fixture packets="+udp.Packets);return;
        }
        var linkedName=Encoding.UTF8.GetBytes("跨服角色");var linkBody=new byte[]{1,101,1,255,(byte)(linkedName.Length+1)}.Concat(linkedName).ToArray();var playerLink=new byte[]{2,39,(byte)(linkBody.Length+1)}.Concat(linkBody).Concat(new byte[]{3}).ToArray();Check(NativePlayerLink.Target(playerLink)=="跨服角色@"+GameNames.World("100"),"native player world preserved for tell replies");Check(NativePlayerLink.Target([2,39,100,1])=="","truncated player link rejected");Check(NativePlayerLink.Target([2,39,247,128])=="","oversized native link length rejected");

        Check(!NativeChatFilters.Valid(new byte[30]),"truncated native filters rejected");
        var filterFixture=new byte[0x150*4];for(int t=0;t<4;t++){BitConverter.TryWriteBytes(filterFixture.AsSpan(t*0x150),(ushort)t);BitConverter.TryWriteBytes(filterFixture.AsSpan(t*0x150+4),0x140u);}
        Check(NativeChatFilters.Valid(filterFixture),"native filter headers accepted");
        Check(NativeChatFilters.Match(filterFixture,10,0,0).Length==0,"disabled chat rows do not leak into tabs");
        filterFixture[0]=99;Check(!NativeChatFilters.Valid(filterFixture),"mismatched native tab rejected");
        var itemFixture=new byte[72];BitConverter.TryWriteBytes(itemFixture.AsSpan(16),12995u);BitConverter.TryWriteBytes(itemFixture.AsSpan(20),3);itemFixture[28]=1;
        var parsedItem=JsonSerializer.SerializeToElement(CharacterReader.Item(itemFixture,0,4));Check(parsedItem.GetProperty("slot").GetInt32()==4&&parsedItem.GetProperty("quantity").GetInt32()==3&&parsedItem.GetProperty("hq").GetBoolean()&&parsedItem.GetProperty("name").GetString()!.Length>0,"original inventory item presentation");
        Check(CharacterReader.Bit([128],7)&&!CharacterReader.Bit([128],8),"collection unlock bit boundaries");

        if(Environment.GetEnvironmentVariable("FF14_TRIAL_UI_RUNTIME")=="1"){
            using var appState=new AppState();appState.Settings.AutoConnect=false;appState.Settings.QQAutoConnect=false;appState.Settings.PreventIdleLogout=false;
            using var scene=new OriginalSettingsPanel(appState);var stop=Task.Run(async()=>{for(int i=0;i<100&&scene.RenderedFrames<30;i++)await Task.Delay(100);scene.Close();});
            try{await scene.Run();await stop;if(scene.RenderedFrames<1||scene.RenderError.Length>0)throw new Exception("Renderer: "+scene.RenderError);Console.WriteLine("REAL DESKTOP RENDERER PASSED frames="+scene.RenderedFrames);}finally{scene.DisposeServices();}
            return;
        }
        if(Environment.GetEnvironmentVariable("FF14_TRIAL_GAME_CONNECT")=="1"){
            var target=NativeInjection.FindGames().Single(g=>g.ContentId.Length>0);NativeInjection.Validate(target,GameProfile.Load());
            using var game=new GameLink();await game.ConnectAsync(target);for(int i=0;i<30&&!game.Identity.online;i++)await Task.Delay(200);Console.WriteLine("CONNECTED "+JsonSerializer.Serialize(game.Identity));if(!game.Identity.online)throw new Exception("No character state");
            using(var reader=new CharacterReader(game.Identity)){Console.WriteLine("Macro entries "+reader.Macros().Length);Console.WriteLine("Tabs "+string.Join(",",reader.TabNames()));Console.WriteLine("Confirmation "+reader.Confirmation());}
            var captureSettings=new Settings{StreamBringToFront=false,HardwareStream=true,StreamHardwareHelper=Path.GetFullPath("work/standalone-trial/artifacts/stream-host/StreamHost.exe"),StreamEncoderPath=Path.Combine(Environment.GetEnvironmentVariable("FF14_TRIAL_COMPONENTS")??Path.GetFullPath("components"),"ffmpeg","ffmpeg.exe")};
            using var capture=new GameCapture(game,_=>true,captureSettings);var started=JsonSerializer.SerializeToElement(await capture.Action(Json(new{action="start",fps=30,bitrate=2000000}),"fixture-device",CancellationToken.None));Console.WriteLine("CAPTURE START "+started);
            using var video=new MemoryStream();using var cancel=new CancellationTokenSource(3000);try{await capture.Video(video,"fixture-device",started.GetProperty("session").GetString()!,()=>true,cancel.Token);}catch(OperationCanceledException){}
            var status=await capture.Action(Json(new{action="status"}),"fixture-device",CancellationToken.None);Console.WriteLine("CAPTURE STATUS "+JsonSerializer.Serialize(status)+" bytes="+video.Length);if(video.Length<1024)throw new Exception("No video packets");
            capture.Stop();Console.WriteLine("Native filters bytes="+game.NativeFilters.Length);if(game.NativeFilters.Length==0)throw new Exception("Native filter read failed");captureSettings.HardwareStream=false;
            var software=JsonSerializer.SerializeToElement(await capture.Action(Json(new{action="start",fps=10,bitrate=2000000}),"fixture-device",CancellationToken.None));
            using var cpuVideo=new MemoryStream();using var cpuCancel=new CancellationTokenSource(2500);try{await capture.Video(cpuVideo,"fixture-device",software.GetProperty("session").GetString()!,()=>true,cpuCancel.Token);}catch(OperationCanceledException){}Console.WriteLine("CPU CAPTURE "+JsonSerializer.Serialize(await capture.Action(Json(new{action="status"}),"fixture-device",CancellationToken.None))+" bytes="+cpuVideo.Length);if(cpuVideo.Length<1024)throw new Exception("No CPU-encoded packets");capture.Stop();
            game.Disconnect();Console.WriteLine("REAL CONNECT/CAPTURE/DISCONNECT PASSED. No chat sent; no party action; no input.");return;
        }
        if(Environment.GetEnvironmentVariable("FF14_TRIAL_GAME_READ")=="1"){
            foreach(var game in NativeInjection.FindGames()){
                Console.WriteLine("GAME "+game);
                if(game.ContentId.Length==0)continue;
                try{NativeInjection.Validate(game,GameProfile.Load());Console.WriteLine("VERSION AND UNMODIFIED ENTRY CHECK PASSED");}catch(Exception e){Console.WriteLine("GUARD "+e.Message);continue;}
                var identity=new GameIdentity(game.Character,GameNames.World(game.World.ToString()),game.ContentId,game.Pid,true);
                foreach(var section in new[]{"character","items","armoury","saddlebag","mounts","minions","currency"}){try{using var reader=new CharacterReader(identity);var result=JsonSerializer.SerializeToElement(reader.Read(section));Console.WriteLine(section+" "+result.ToString()[..Math.Min(180,result.ToString().Length)]);}catch(Exception e){Console.WriteLine(section+" ERROR "+e.Message);}}
                Console.WriteLine("PARTY "+JsonSerializer.Serialize(NativeInjection.ReadParty(identity)));
            }
            return;
        }

        if(Environment.GetEnvironmentVariable("FF14_TRIAL_CHECK_COMPONENTS")=="1"){
            var bundled=Path.Combine(AppContext.BaseDirectory,"组件","NapCat");Directory.CreateDirectory(bundled);
            var archive=Path.Combine(bundled,"download-"+PhoneNapCat.Version+".zip");
            File.Copy(Path.Combine(Environment.CurrentDirectory,"work","standalone-trial","artifacts","components","NapCat",Path.GetFileName(archive)),archive,true);
            var prepared=Path.Combine(root,"offline-components");
            await PhoneNapCat.Prepare(prepared,_=>{},CancellationToken.None);
            Check(File.Exists(Path.Combine(prepared,"runtime-"+PhoneNapCat.Version,"node.exe")),"offline preparation extracts node");
            await PhoneNapCat.Prepare(prepared,_=>{},CancellationToken.None);
            File.AppendAllText(Path.Combine(prepared,"runtime-"+PhoneNapCat.Version,"index.js"),"/* integrity test */");
            bool corrupt=false;try{await PhoneNapCat.Prepare(prepared,_=>{},CancellationToken.None);}catch(IOException){corrupt=true;}
            Check(corrupt,"modified runtime rejected");
            File.Move(archive,archive+".checking");
            try{bool absent=false;try{await PhoneNapCat.Prepare(Path.Combine(root,"missing-components"),_=>{},CancellationToken.None);}catch(IOException e){absent=e.Message.Contains("不进行联网下载");}Check(absent,"missing bundle rejected without downloading");}
            finally{File.Move(archive+".checking",archive);}
        }

        Console.WriteLine($"{OriginalInputChecks.Run()} original streaming-control checks passed using fake input writers.");
        Console.WriteLine($"{OriginalUiChecks.Run()} original UI checks passed without starting a desktop renderer or game.");
        using (var stream = new MemoryStream(Encoding.ASCII.GetBytes("POST /send HTTP/1.1\r\nContent-Length: 2\r\nAuthorization: Bearer fake\r\n\r\n{}"))) { var request = await PhoneServer.Request.Read(stream, CancellationToken.None); Check(request.Method == "POST" && request.Body.Length == 2, "request framing"); }
        await Reject("POST /send HTTP/1.1\r\nContent-Length: 2\r\ncontent-length: 2\r\n\r\n{}", "duplicate headers");
        await Reject("POST /send HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n", "unsupported framing");
        await Reject("POST /send HTTP/1.1\r\nContent-Length: -1\r\n\r\n", "negative length");
        await Reject("POST /send HTTP/1.1\r\nContent-Length: 8193\r\n\r\n", "bounded request body");
        await Reject("GET /send HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}", "GET body rejected");
        await Reject("POST https://elsewhere/send HTTP/1.1\r\n\r\n", "absolute request target rejected");
        await Reject("DELETE /send HTTP/1.1\r\n\r\n", "method allowlist");
        Check(GameLink.SeStringText(Encoding.UTF8.GetBytes("你好")) == "你好", "UTF8 text");
        Check(GameLink.SeStringText([65, 2, 19, 3, 80, 81, 3, 66]) == "AB", "SeString payload stripping");
        Check(GameLink.SeStringText([65, 2, 19, 20, 80]) == "A", "truncated payload stops safely");
        Check(GameLink.SeStringText([65, 0, 66]) == "A", "NUL boundary");
        Check(!GameLink.ChannelNames.Values.Any(n => n.Contains("国服") || n.Contains("卫月")), "native channel set");
        var row = new byte[400]; BitConverter.TryWriteBytes(row.AsSpan(), 12345678901234567UL); BitConverter.TryWriteBytes(row.AsSpan(68), (ushort)120); row[83] = 8; row[84] = 2; Encoding.UTF8.GetBytes("队长").CopyTo(row, 168); Encoding.UTF8.GetBytes("测试招募").CopyTo(row, 200);
        var decoded = GameLink.ParseRecruitRow(row); Check(decoded?.id == "12345678901234567" && decoded.leader == "队长" && decoded.comment == "测试招募", "recruitment IDs and UTF8");
        Check(decoded!.filledSlots == 2 && decoded.totalSlots == 8, "recruitment slot counts");
        Check(GameLink.ParseRecruitRow(new byte[400]) == null, "empty recruitment entry");
        bool invalid = false; row[84] = 9; try { GameLink.ParseRecruitRow(row); } catch (InvalidDataException) { invalid = true; } Check(invalid, "recruitment bounds");
        var profile = GameProfile.Load(); Check(profile.Addresses.Count >= 21 && profile.Addresses.All(x => x.Value is >4096 && x.Value < profile.ImageSize), "pinned native profile");
        Check(Convert.FromBase64String(profile.PrintPrologue).Length == 16 && Convert.FromBase64String(profile.ListingPrologue).Length == 16, "hook prologues");
        using (var target = System.Diagnostics.Process.GetCurrentProcess()) { bool denied = false; try { NativeInjection.Validate(new(target.Id, target.StartTime.ToUniversalTime().Ticks, target.MainModule!.FileName, "fixture", "", "", 0), profile); } catch (IOException) { denied = true; } Check(denied, "non-game process rejected without injection"); }
        Directory.CreateDirectory(root);
        byte[] secret = Encoding.UTF8.GetBytes("synthetic-certificate-secret"); var protectedBytes = PrivateData.Protect(secret); Check(!protectedBytes.AsSpan().SequenceEqual(secret) && PrivateData.Unprotect(protectedBytes).SequenceEqual(secret), "Windows credential protection");
        var settings = new Settings(); var pairing = new Pairing(settings); string id = Guid.NewGuid().ToString(), pin = new('a', 64), deviceSecret = new('b', 64);
        var requestData = Json(new { id, pin, secret = deviceSecret, name = "Test\r\nPhone" });
        var pending = JsonSerializer.SerializeToElement(pairing.Request(requestData, true, pin)); Check(!pending.GetProperty("approved").GetBoolean() && pending.GetProperty("code").GetString()!.Length == 6, "explicit pending approval");
        Check(pairing.Pending.Single().Name == "TestPhone", "device name sanitation");
        pairing.Approve(id); var accepted = JsonSerializer.SerializeToElement(pairing.Request(requestData, false, pin)); string token = accepted.GetProperty("token").GetString()!;
        Check(accepted.GetProperty("approved").GetBoolean() && pairing.Authorized("Bearer " + token), "approved device token");
        Check(!pairing.Authorized("Bearer " + new string('0', 64)), "wrong token rejected");
        Check(!File.ReadAllText(Path.Combine(root, "settings.json")).Contains(token), "no plaintext stored token");
        pairing.RevokeAll(); Check(!pairing.Authorized("Bearer " + token), "revocation");
        var fish = new FishingQueue(); long at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 20;
        fish.Action(Json(new { action = "sync", enabled = true, rows = new[] { new { key = "one", title = "Fish", body = "Window", at, expires = at + 10000 } } }), "a");
        Check(fish.Due("b", 0).Length == 0, "reminders isolated by device"); await Task.Delay(40);
        var due = fish.Due("a", 0); Check(due.Length == 1 && due[0].key == "one", "scheduled reminder delivery"); Check(fish.Due("a", due[0].id).Length == 0, "reminder cursor");
        int port; using (var socket = new TcpListener(IPAddress.Loopback, 0)) { socket.Start(); port = ((IPEndPoint)socket.LocalEndpoint).Port; }
        using var app = new AppState(); app.Settings.Port = port; app.Server.Start();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(app.Server.Fingerprint, StringComparison.OrdinalIgnoreCase) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(20) };
        using (var info = await http.GetAsync("/pair-info")) Check(info.IsSuccessStatusCode, "TLS bootstrap");
        using (var noAuth = await http.GetAsync("/poll")) Check(noAuth.StatusCode == HttpStatusCode.Unauthorized, "TLS route authorization");
        string wireId = Guid.NewGuid().ToString(); var body = new { id = wireId, pin = app.Server.Fingerprint, secret = deviceSecret, name = "Wire fixture" };
        using (var pair = await http.PostAsync("/pair-request", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"))) Check(pair.IsSuccessStatusCode, "TLS pairing request");
        app.Pairing.Approve(wireId);
        using (var status = await http.PostAsync("/pair-status", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"))) { using var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync()); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("token").GetString()); }
        using (var poll = await http.GetAsync("/poll?after=0")) { using var doc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync()); var r = doc.RootElement; Check(r.GetProperty("edition").GetString() == "standalone" && !r.TryGetProperty("worldAccount", out _), "standalone snapshot"); Check(!r.GetProperty("pageNavigation").EnumerateArray().Any(x => new[] { "office", "glamour", "itemSearch", "collections" }.Contains(x.GetProperty("id").GetString())), "removed navigation"); }
        foreach (string endpoint in new[] { "/omni-glamour", "/omni-items", "/world-board", "/world-permission" }) using (var response = await http.PostAsync(endpoint, new StringContent("{}"))) Check(response.StatusCode == HttpStatusCode.NotFound, "removed endpoint " + endpoint);
        using (var send = await http.PostAsync("/send", new StringContent(JsonSerializer.Serialize(new { channel = "默语", target = "", text = "synthetic", contentId = "123", requestId = Guid.NewGuid().ToString() })))) { using var doc = JsonDocument.Parse(await send.Content.ReadAsStringAsync()); Check(!doc.RootElement.GetProperty("ok").GetBoolean(), "offline send never claims success"); }
        app.Pairing.RevokeAll(); using (var revoked = await http.GetAsync("/poll")) Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "TLS token revocation");
        Console.WriteLine($"{count} managed checks passed. TLS tested on loopback. No game process was injected, captured, or controlled. Test storage: {root}");
    }
}




