using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Trial;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(-4);
        ApplicationConfiguration.Initialize();
        if(args.Length==2&&args[0]=="--replace"&&int.TryParse(args[1],out int oldPid)){try{using var old=Process.GetProcessById(oldPid);if(!old.WaitForExit(8000))return;}catch(ArgumentException){}}
        using var single = new Mutex(true, @"Local\FF14ChatStandaloneTrial", out bool owner);
        if (!owner) { MessageBox.Show("试用版服务端已在运行，请查看系统托盘。", "狒狒食柿"); return; }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write(e.ExceptionObject as Exception ?? new Exception("未处理异常"));
        using var state = new AppState();
        using var window = new MainWindow(state, args.Contains("--silent"));
        Application.Run(window);
    }
    [System.Runtime.InteropServices.DllImport("user32")] static extern bool SetProcessDpiAwarenessContext(nint value);
}

internal static class Log
{
    static Log() { }
    static readonly object Gate = new();
    public static readonly string Root = Environment.GetEnvironmentVariable("FF14_TRIAL_DATA_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoYanjiu", "FF14ChatTrial");
    public static void Write(Exception e) { lock (Gate) { try { Directory.CreateDirectory(Root); File.AppendAllText(Path.Combine(Root, "error.log"), $"{DateTimeOffset.Now:O} {e.GetType().Name}: {e.Message}\n"); } catch { } } }
}

internal sealed class Settings : OriginalDesktop.ModuleSettings
{
    public new int Port { get; set; } = 18766;
    public bool AutoConnect { get; set; } = true;
    public bool Silent { get; set; }
    public bool StartWithWindows {get;set;}
    public bool PreventIdleLogout { get; set; }
    public string PreferredCharacter { get; set; } = "";
    public Dictionary<string, Device> Devices { get; set; } = new();
    public Dictionary<string,string> DeviceRemarks {get;set;}=new();
    public static Settings Load()
    {
        var p = Path.Combine(Log.Root, "settings.json");
        if (!File.Exists(p)) return new();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(p)) ?? new(); }
        catch (Exception e) { Log.Write(e); throw new InvalidDataException("试用版配置无法读取，未覆盖原配置。", e); }
    }
    static readonly object saveGate=new();
    public void Save()
    {
        lock(saveGate){
        Directory.CreateDirectory(Log.Root);
        string path = Path.Combine(Log.Root, "settings.json"), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
        }
    }
}
internal sealed record Device(string Name, string TokenHash, DateTimeOffset Authorized);
internal sealed record PendingDevice(string Id, string Name, string SecretHash, string Code, DateTimeOffset Expires)
{
    public string Token { get; set; } = "";
    public bool Rejected { get; set; }
}

internal sealed class AppState : IDisposable
{
    public readonly Settings Settings = Settings.Load();
    public readonly GameLink Game = new();
    public readonly FishingQueue Fishing = new();
    public readonly GameCapture Capture;
    public readonly Pairing Pairing;
    public readonly PhoneServer Server;
    public readonly NativeDesktop.AntiIdle AntiIdle;
    readonly object noticeGate=new();readonly List<OriginalDesktop.PhoneChatCore.ChatEntry> hostNotices=new();
    public void PublishNotice(OriginalDesktop.PhoneChatCore.ChatEntry entry){lock(noticeGate){entry.id=NoticeIds.Next();entry.time=DateTimeOffset.Now.ToString("O");hostNotices.Add(entry);while(hostNotices.Count>128)hostNotices.RemoveAt(0);}}
    public OriginalDesktop.PhoneChatCore.ChatEntry[] Notices(long after){lock(noticeGate)return hostNotices.Where(e=>e.id>after).ToArray();}
    public AppState() {
        Pairing = new(Settings); Capture = new(Game,Pairing.Authorized,Settings); Server = new(this);
        AntiIdle = new(() => {
            var identity=Game.Identity; if(!identity.online)return null;
            try { using var process=System.Diagnostics.Process.GetProcessById(identity.pid); return new NativeDesktop.AntiIdle.Target(identity.pid,process.StartTime.ToUniversalTime().Ticks,process.MainWindowHandle,identity.contentId); }catch{return null;}
        },target => { var identity=Game.Identity; if(!identity.online||identity.pid!=target.Pid||identity.contentId!=target.CharacterId)return false;
            return NativeInjection.FindGames().Any(g=>g.Pid==target.Pid&&g.Started==target.StartedUtcTicks&&g.ContentId==target.CharacterId); });
        AntiIdle.Enabled=Settings.PreventIdleLogout;
    }
    public void Dispose() { AntiIdle.Dispose(); Fishing.Dispose(); Server.Dispose(); Capture.Dispose(); Game.Dispose(); }
}

internal sealed class Pairing(Settings settings)
{
    readonly object gate = new();
    readonly Dictionary<string, PendingDevice> requests = new();
    public PendingDevice[] Pending { get { lock (gate) { Expire(); return requests.Values.Where(x => !x.Rejected && x.Token == "").ToArray(); } } }
    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
    static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    void Expire() { foreach (var k in requests.Where(x => x.Value.Expires < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) requests.Remove(k); }
    public object Request(JsonElement json, bool create, string fingerprint)
    {
        string id = json.GetProperty("id").GetString() ?? "", secret = json.GetProperty("secret").GetString() ?? "";
        if (!Guid.TryParse(id, out _) || secret.Length != 64 || !secret.All(Uri.IsHexDigit)) throw new InvalidDataException("设备授权请求无效");
        lock (gate)
        {
            Expire();
            if (!requests.TryGetValue(id, out var r))
            {
                if (!create || requests.Count >= 12) throw new InvalidDataException("授权请求已过期或已满");
                if (!Equal(json.GetProperty("pin").GetString() ?? "", fingerprint)) throw new InvalidDataException("服务器证书不一致");
                string deviceName = new((json.GetProperty("name").GetString() ?? "手机").Where(c => !char.IsControl(c)).Take(64).ToArray());
                r = new(id, deviceName.Length == 0 ? "手机" : deviceName, Hash(secret), RandomNumberGenerator.GetInt32(100000, 1000000).ToString(), DateTimeOffset.UtcNow.AddMinutes(5));
                requests.Add(id, r);
            }
            if (!Equal(r.SecretHash, Hash(secret)) || r.Rejected) throw new UnauthorizedAccessException("授权请求已拒绝或无效");
            return new { code = r.Code, approved = r.Token.Length > 0, token = r.Token, expires = r.Expires };
        }
    }
    public void Approve(string id)
    {
        lock (gate)
        {
            Expire();
            if (!requests.TryGetValue(id, out var r) || r.Rejected || r.Token.Length > 0) throw new InvalidOperationException("请求已失效");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            settings.Devices[id] = new(r.Name, Hash(token), DateTimeOffset.UtcNow);
            try { settings.Save(); r.Token = token; } catch { settings.Devices.Remove(id); throw; }
        }
    }
    public void Reject(string id) { lock (gate) { if (requests.TryGetValue(id, out var r)) r.Rejected = true; } }
    public bool Authorized(string value)
    {
        if (!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length != 71) return false;
        lock (gate) return settings.Devices.Values.Any(d => Equal(d.TokenHash, Hash(value[7..])));
    }
    public void Revoke(string id){lock(gate){settings.Devices.Remove(id);requests.Remove(id);settings.Save();}}
    public void RevokeAll() { lock (gate) { settings.Devices.Clear(); requests.Clear(); settings.Save(); } }
}



