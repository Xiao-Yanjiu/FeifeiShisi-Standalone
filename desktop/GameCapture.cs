using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Trial;
internal sealed class GameCapture(GameLink game, Func<string,bool>? authorized = null,Settings? settings=null) : IDisposable
{
    readonly OriginalStreamInput originalInput = new(authorized ?? (_ => false));
    readonly SemaphoreSlim lifecycle = new(1);
    readonly object gate = new();
    readonly Queue<byte[]> packets = new();
    TaskCompletionSource signal = NewSignal();
    PhoneUdpVideoSession? udp;
    Process? process; CancellationTokenSource? cancellation;
    readonly HashSet<ushort> held = [];
    readonly HashSet<string> mouseButtons = [];
    string owner = "", session = "", failure = ""; nint window;
    long touched, inputTouched, inputSequence, controlEpoch; bool control, reader, needsKey = true;
    int sourcePid, width, height; long frames; string sourceCharacter = "";
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    // A lazy timer avoids touching desktop input or capture APIs until the user starts a session.
    System.Threading.Timer? lease;
    public bool Available => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && File.Exists(Helper) && File.Exists(Encoder);
    string Helper => !string.IsNullOrWhiteSpace(settings?.StreamHardwareHelper)?settings.StreamHardwareHelper:Path.Combine(AppContext.BaseDirectory, "组件", "StreamHost.exe");
    string Encoder => !string.IsNullOrWhiteSpace(settings?.StreamEncoderPath)?settings.StreamEncoderPath:Path.Combine(AppContext.BaseDirectory, "组件", "ffmpeg", "ffmpeg.exe");
    public async Task<object> Action(JsonElement r, string auth, CancellationToken token)
    {
        string action = r.GetProperty("action").GetString() ?? "";
        if(action is "prepare" or "prepare-status"){if(!Available)throw new IOException("缺少免安装包中的画面组件，请完整解压服务端目录");return new{ok=true,ready=true,stage="ready"};}
        if(action=="udp-session"){lock(gate){CheckOwner(auth);if(udp==null||process==null||r.GetProperty("session").GetString()!=session)throw new IOException("UDP视频会话已失效");touched=Environment.TickCount64;return new{ok=true,protocol="pcu1",key=udp.Secret,port=settings?.StreamUdpExternalPort is >=1024 and <=65535?settings.StreamUdpExternalPort:udp.Port,localPort=udp.Port};}}
        if(action=="resize"){int w=r.GetProperty("width").GetInt32(),h=r.GetProperty("height").GetInt32();if(w is <320 or >3840||h is <320 or >3840)throw new InvalidDataException("手机画面尺寸无效");nint target;lock(gate){CheckOwner(auth);if(process==null)throw new IOException("串流已停止");target=window;touched=Environment.TickCount64;}if(!GetWindowRect(target,out var outer)||!GetClientRect(target,out var client)||!SetWindowPos(target,0,0,0,w+outer.Right-outer.Left-client.Right,h+outer.Bottom-outer.Top-client.Bottom,0x4016))throw new IOException("无法调整游戏窗口尺寸");await Task.Delay(300,token);if(!GetClientRect(target,out var current)||current.Right!=w||current.Bottom!=h)throw new IOException("游戏未接受新的窗口尺寸");return new{ok=true,width=w,height=h};}
        if(action=="focus"){lock(gate){CheckOwner(auth);if(process==null)throw new IOException("请先开始串流");}return await originalInput.Focus(token);}
        if(action=="status") { lock(gate) {CheckOwner(auth);touched=Environment.TickCount64;var state=System.Text.Json.JsonSerializer.SerializeToElement(originalInput.Status());return new{ok=true,running=process!=null&&!process.HasExited,error=failure,frames,controlEnabled=state.GetProperty("controlEnabled").GetBoolean(),controlSequence=state.GetProperty("controlSequence").GetInt64(),foreground=state.GetProperty("foreground").GetBoolean(),cursor=state.GetProperty("cursor").GetBoolean(),x=state.GetProperty("x").GetDouble(),y=state.GetProperty("y").GetDouble()};}}
        await lifecycle.WaitAsync(token);
        try
        {
            if (action == "stop") { lock (gate) { CheckOwner(auth); StopLocked(); } return new { ok = true }; }
            if (action == "status") { lock (gate) { CheckOwner(auth); touched = Environment.TickCount64; return new { ok = true, running = process != null && !process.HasExited, error = failure, frames }; } }
            if (action != "start") throw new InvalidDataException("未知画面操作");
            if (!Available) throw new InvalidOperationException("缺少免安装包中的画面组件，或 Windows 版本不支持窗口捕获");
            var who = game.Identity; if (!who.online) throw new InvalidOperationException("请先连接已经登录的游戏角色");
            int fps = r.TryGetProperty("fps", out var f) ? f.GetInt32() : 30;
            if (fps is not (10 or 30 or 60)) throw new InvalidDataException("帧率参数无效");
            using var target = Process.GetProcessById(who.pid); if (target.ProcessName != "ffxiv_dx11" || target.MainWindowHandle == 0) throw new IOException("游戏窗口已失效");
            if (!GetClientRect(target.MainWindowHandle, out var rect) || rect.Right < 2 || rect.Bottom < 2) throw new IOException("游戏窗口尺寸不可用");
            lock (gate) { CheckOwner(auth); StopLocked(); owner = auth; session = Guid.NewGuid().ToString("N"); sourcePid = who.pid; sourceCharacter = who.contentId; window = target.MainWindowHandle; width = rect.Right & ~1; height = rect.Bottom & ~1;double scale=Math.Min(1d,Math.Min(3840d/width,2160d/height));width=(int)(width*scale)&~1;height=(int)(height*scale)&~1; touched = Environment.TickCount64; frames = 0; }
            int bitrate=r.TryGetProperty("bitrate",out var rate)?rate.GetInt32():8000000;if(bitrate is not (2000000 or 8000000 or 20000000))throw new InvalidDataException("码率参数无效");
            var start = new ProcessStartInfo(Helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (string arg in new[] { who.pid.ToString(), fps.ToString(), bitrate.ToString(), string.IsNullOrEmpty(settings?.StreamAdapter)?"auto":settings.StreamAdapter, Encoder, settings?.StreamGpuDirect==false?"cpu":"gpu",settings?.HardwareStream==false?"software":"hardware" }) start.ArgumentList.Add(arg);
            var encoder = new Process { StartInfo = start };
            if (!encoder.Start()) throw new IOException("无法启动独立画面组件");
            var cts = new CancellationTokenSource();
            lock (gate) { process = encoder; originalInput.Attach(encoder,window,auth,session); cancellation = cts; needsKey = true; failure = ""; }
            if(settings?.StreamTransport=="udp"){
                int port=settings.StreamUdpPort is >=1024 and <=65535?settings.StreamUdpPort:settings.Port;
                udp=new PhoneUdpVideoSession(port,bitrate,fps,()=>{lock(gate){touched=Environment.TickCount64;if(!(authorized?.Invoke(auth)??false))StopLocked();}},()=>{lock(gate)originalInput.CloseControl();},()=>{try{encoder.StandardInput.WriteLine("RATE 2000000");}catch{}});
            }
            if(settings?.StreamBringToFront==true)await originalInput.Focus(token);
            lease ??= new System.Threading.Timer(_ => Watch(), null, 100, 100);
            _ = ReadEncoder(encoder, cts.Token);
            _ = ReadErrors(encoder);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(15000);
            while (true)
            {
                Task pending;
                lock (gate) { if (failure.Length > 0) throw new IOException(failure); if (frames > 0) break; pending = signal.Task; }
                await pending.WaitAsync(deadline.Token);
            }
            lock (gate) return new { ok = true, width, height, fps, codec = "h264", transport = udp==null?"tcp":"udp", session, inputSession = session, udpKey=udp?.Secret??"",udpPort=udp==null?0:settings?.StreamUdpExternalPort is >=1024 and <=65535?settings.StreamUdpExternalPort:udp.Port };
        }
        catch { if (action == "start") { lock (gate) { if (owner == auth) StopLocked(); } } throw; }
        finally { lifecycle.Release(); }
    }
    void CheckOwner(string auth) { if (owner.Length > 0 && owner != auth) throw new InvalidOperationException("游戏画面正在由另一台设备使用"); }
    static bool IsKey(byte[] bytes) { for (int i = 0; i + 4 < bytes.Length; i++) if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1 && (bytes[i + 3] & 31) == 5) return true; return false; }
    async Task ReadEncoder(Process encoder, CancellationToken token)
    {
        try
        {
            var size = new byte[4]; var stream = encoder.StandardOutput.BaseStream; var bootstrap = new H264Bootstrap();
            while (!token.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(size, token); int length = BitConverter.ToInt32(size); if (length is <4 or >4194304) throw new IOException("画面编码数据长度无效");
                var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token); bytes = bootstrap.Prepare(bytes);
                lock (gate)
                {
                    if (process != encoder) return;
                    frames++; udp?.Offer(bytes); if (packets.Count >= 2) { packets.Clear(); needsKey = true; }
                    if (!needsKey || IsKey(bytes)) { needsKey = false; packets.Enqueue(bytes); }
                    signal.TrySetResult(); signal = NewSignal();
                }
            }
        }
        catch (Exception e) { lock (gate) { if (process == encoder && !token.IsCancellationRequested) { failure = "画面采集已中断：" + e.Message; ReleaseInput(); signal.TrySetResult(); } } }
    }
    async Task ReadErrors(Process encoder)
    {
        try { while (await encoder.StandardError.ReadLineAsync() is { } line) { if (line.StartsWith("H264 ")) continue; lock (gate) { if (process == encoder) { failure = line[..Math.Min(line.Length, 500)]; signal.TrySetResult(); } } } } catch { }
    }
    public async Task Video(Stream output, string auth, string id, Func<bool> authorized, CancellationToken token)
    {
        lock (gate) { CheckOwner(auth); if (process == null || session != id || reader) throw new IOException("画面会话已失效或已连接"); reader = true; packets.Clear(); needsKey = true; }
        try
        {
            await output.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-phonechat-h264\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), token);
            while (!token.IsCancellationRequested && authorized())
            {
                byte[]? bytes = null; Task pending;
                lock (gate) { if (session != id || process == null || failure.Length > 0) return; touched = Environment.TickCount64; if (packets.Count > 0) bytes = packets.Dequeue(); pending = signal.Task; }
                if (bytes == null) { await pending.WaitAsync(TimeSpan.FromSeconds(5), token); continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(2500);
                await output.WriteAsync(BitConverter.GetBytes(bytes.Length), deadline.Token); await output.WriteAsync(bytes, deadline.Token); await output.FlushAsync(deadline.Token);
            }
        }
        finally { lock (gate) { if (session == id) { reader = false; needsKey = true; packets.Clear(); ReleaseInput(); } } }
    }
    public object OriginalInput(string path,JsonElement data,string auth)
    {
        lock(gate){CheckOwner(auth);var who=game.Identity;if(process==null||!who.online||sourcePid!=who.pid||sourceCharacter!=who.contentId)throw new IOException("游戏角色或串流会话已变化");touched=Environment.TickCount64;return originalInput.Apply(path,data,auth);}
    }
    public object Input(JsonElement r, string auth)
    {
        lock (gate)
        {
            CheckOwner(auth); if (r.GetProperty("session").GetString() != session || process == null || sourcePid != game.Identity.pid) throw new IOException("控制会话已失效");
            string action = r.GetProperty("action").GetString() ?? "";
            if (action == "foreground") { if (IsIconic(window)) ShowWindowAsync(window, 9); bool foreground = SetForegroundWindow(window); return new { ok = true, foreground }; }
            if (action == "enable") { ReleaseInput(); control = r.GetProperty("enabled").GetBoolean(); if (control && GetForegroundWindow() != window) { control = false; throw new IOException("请先将所选游戏窗口置前，再启用控制"); } return new { ok = true, enabled = control, epoch = controlEpoch }; }
            if (!r.TryGetProperty("epoch", out var epoch) || epoch.GetInt64() != controlEpoch) throw new IOException("控制模式已切换，旧输入已丢弃");
            long sequence = r.GetProperty("sequence").GetInt64(); if (sequence <= inputSequence) return new { ok = true, ignored = true }; inputSequence = sequence;
            if (!control || GetForegroundWindow() != window) { ReleaseInput(); throw new IOException("游戏不在前台，控制输入已释放"); }
            if (action == "pointer")
            {
                float x = r.GetProperty("x").GetSingle(), y = r.GetProperty("y").GetSingle(); string button = r.GetProperty("button").GetString() ?? ""; bool pointerDown = r.GetProperty("down").GetBoolean();
                if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1 || button is not ("left" or "right")) throw new InvalidDataException("指针参数无效");
                var origin = new SystemPoint(); if (!GetClientRect(window, out var client) || !ClientToScreen(window, ref origin)) throw new IOException("游戏窗口位置不可用");
                int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79); if (width < 2 || height < 2) throw new IOException("桌面尺寸不可用");
                var move = new MouseEvent { X = (int)Math.Round((origin.X + x * (client.Right - 1) - left) * 65535d / (width - 1)), Y = (int)Math.Round((origin.Y + y * (client.Bottom - 1) - top) * 65535d / (height - 1)), Flags = 0xC001 };
                if (SendMouse(1, [move], 40) != 1) throw new IOException("Windows 未接受鼠标移动");
                if (pointerDown ? mouseButtons.Add(button) : mouseButtons.Remove(button)) MouseButton(button, pointerDown);
                inputTouched = touched = Environment.TickCount64; return new { ok = true };
            }
            var keys = new Dictionary<string, ushort> { ["W"] = 87, ["A"] = 65, ["S"] = 83, ["D"] = 68, ["SPACE"] = 32, ["NUM0"] = 96, ["ESC"] = 27, ["M"] = 77, ["UP"] = 38, ["DOWN"] = 40, ["LEFT"] = 37, ["RIGHT"] = 39 };
            if (action != "key" || !keys.TryGetValue(r.GetProperty("key").GetString() ?? "", out var key)) throw new InvalidDataException("未知控制输入");
            bool down = r.GetProperty("down").GetBoolean(); if (down ? held.Add(key) : held.Remove(key)) SendKey(key, down); inputTouched = touched = Environment.TickCount64;
            return new { ok = true };
        }
    }
    void Watch() { lock (gate) { if (process == null) return; originalInput.Tick(); var who = game.Identity; if (sourcePid != who.pid || sourceCharacter != who.contentId || !who.online || Environment.TickCount64 - touched > 15000) { StopLocked(); return; } if ((held.Count > 0 || mouseButtons.Count > 0) && (GetForegroundWindow() != window || Environment.TickCount64 - inputTouched > 1500)) ReleaseInput(); } }
    void ReleaseInput() { controlEpoch++; foreach (var key in held) try { SendKey(key, false); } catch { } held.Clear(); foreach (var button in mouseButtons) try { MouseButton(button, false); } catch { } mouseButtons.Clear(); control = false; }
    void StopLocked()
    {
        udp?.Dispose();udp=null;originalInput.Close(); ReleaseInput(); cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; var old = process; process = null; session = owner = failure = ""; sourcePid = 0; window = 0; reader = false; inputSequence = 0; packets.Clear(); signal.TrySetResult(); signal = NewSignal();
        if (old != null) _ = Task.Run(async () => { try { if (!old.HasExited) { old.Kill(true); await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); } } catch (Exception e) { Log.Write(e); } finally { old.Dispose(); } });
    }
    public void Stop() { lock (gate) StopLocked(); }
    public void Dispose() { Stop(); lease?.Dispose(); }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct InputEvent { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Vk; [FieldOffset(12)] public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct MouseEvent { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public int X; [FieldOffset(12)] public int Y; [FieldOffset(20)] public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct SystemPoint { public int X, Y; }
    static void MouseButton(string button, bool down) { uint flag = button == "left" ? down ? 2u : 4u : down ? 8u : 16u; if (SendMouse(1, [new() { Flags = flag }], 40) != 1) throw new IOException("Windows 未接受鼠标按键"); }
    static void SendKey(ushort key, bool down) { if (SendInput(1, [new() { Type = 1, Vk = key, Flags = down ? 0u : 2u }], 40) != 1) throw new IOException("Windows 未接受控制输入"); }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32")] static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32", SetLastError = true)] static extern uint SendInput(uint count, InputEvent[] inputs, int size);
    [DllImport("user32")] static extern nint GetForegroundWindow();
    [DllImport("user32")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32")] static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32")] static extern bool IsIconic(nint window);
    [DllImport("user32")] static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32", EntryPoint = "SendInput", SetLastError = true)] static extern uint SendMouse(uint count, MouseEvent[] inputs, int size);
    [DllImport("user32")] static extern bool ClientToScreen(nint window, ref SystemPoint point);
    [DllImport("user32")] static extern int GetSystemMetrics(int index);
}

