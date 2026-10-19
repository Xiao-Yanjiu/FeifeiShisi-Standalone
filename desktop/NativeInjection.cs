using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Trial;

internal static class NativeInjection
{
    const uint ReadAccess = 0x410, InjectAccess = 0x43A;
    [DllImport("kernel32", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(SafeProcessHandle handle, nint address, byte[] buffer, nuint length, out nuint read);
    [DllImport("kernel32", SetLastError = true)] static extern bool WriteProcessMemory(SafeProcessHandle handle, nint address, byte[] buffer, nuint length, out nuint written);
    [DllImport("kernel32", SetLastError = true)] static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32", SetLastError = true)] static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint type);
    [DllImport("kernel32", SetLastError = true)] static extern nint CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stack, nint entry, nint param, uint flags, out uint id);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(nint handle, uint timeout);
    [DllImport("kernel32")] static extern bool GetExitCodeThread(nint thread, out uint result);
    [DllImport("kernel32")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string module);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32")] static extern bool FreeLibrary(nint module);

    public static void Validate(GameProcess game, GameProfile profile)
    {
        using var p = Process.GetProcessById(game.Pid);
        if (p.StartTime.ToUniversalTime().Ticks != game.Started || p.MainModule?.FileName != game.Path || !p.ProcessName.Equals("ffxiv_dx11", StringComparison.OrdinalIgnoreCase)) throw new IOException("游戏进程已退出或被替换");
        if (p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("dalamud", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("此游戏进程正在使用卫月，请为试用版选择原生游戏进程");
        using var file = File.OpenRead(game.Path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(profile.ImageSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("当前游戏版本尚未适配，未进行注入");
        using var handle = OpenProcess(ReadAccess, false, game.Pid);
        if (handle.IsInvalid) throw new IOException("无法读取游戏进程；请确认服务端和游戏权限一致");
        var expected = Convert.FromBase64String(profile.PrintPrologue);
        var prologue = Read(handle, p.MainModule!.BaseAddress + checked((int)profile.Addresses["PrintMessage"]), expected.Length);
        if (!prologue.SequenceEqual(expected)) throw new InvalidOperationException("游戏聊天入口已被其他组件修改，未重复挂接");
        var listingExpected = Convert.FromBase64String(profile.ListingPrologue);
        if (!Read(handle, p.MainModule.BaseAddress + (int)profile.Addresses["ReceiveListing"], listingExpected.Length).SequenceEqual(listingExpected)) throw new InvalidOperationException("游戏招募入口已被其他组件修改，未重复挂接");
    }
    internal static SafeProcessHandle OpenRead(int pid)=>OpenProcess(ReadAccess,false,pid);
    internal static byte[] Read(SafeProcessHandle handle, nint address, int length)
    {
        if (length < 0 || length > 1024 * 1024 || address == 0) throw new IOException("内存读取范围无效");
        var b = new byte[length]; if (!ReadProcessMemory(handle, address, b, (nuint)length, out var count) || count != (nuint)length) throw new IOException("游戏数据尚未就绪"); return b;
    }
    public static GameProcess[] FindGames()
    {
        GameProfile? profile = null; try { profile = GameProfile.Load(); } catch { }
        var games = new List<GameProcess>();
        foreach (var p in Process.GetProcessesByName("ffxiv_dx11")) using (p)
        {
            try
            {
                string name = "", cid = ""; ushort world = 0;
                if (profile != null)
                {
                    using var h = OpenProcess(ReadAccess, false, p.Id);
                    if (!h.IsInvalid)
                    {
                        var state = Read(h, p.MainModule!.BaseAddress + (int)profile.Addresses["PlayerState"], 144);
                        if (state[0] != 0) { int end = Array.IndexOf(state, (byte)0, 1, 64); if (end < 0) end = 65; name = Encoding.UTF8.GetString(state, 1, end - 1); cid = BitConverter.ToUInt64(state, 104).ToString(); }
                        var actor = (nint)BitConverter.ToInt64(Read(h, p.MainModule.BaseAddress + (int)profile.Addresses["ObjectManager"] + 32, 8));
                        if (actor != 0 && BitConverter.ToUInt64(Read(h, actor + 9048, 8)) == (ulong.TryParse(cid, out var id) ? id : 0)) world = BitConverter.ToUInt16(Read(h, actor + 9058, 2));
                    }
                }
                games.Add(new(p.Id, p.StartTime.ToUniversalTime().Ticks, p.MainModule?.FileName ?? "", p.MainWindowTitle, name, cid, world));
            }
            catch { try { games.Add(new(p.Id, 0, "", p.MainWindowTitle, "", "", 0, "无法读取游戏角色，请以与游戏相同的权限运行服务端")); } catch { } }
        }
        return games.ToArray();
    }
    public static void Start(GameProcess game, GameProfile profile, string pipeName)
    {
        Validate(game, profile);
        string native = ExtractBridge();
        using var process = Process.GetProcessById(game.Pid);
        using var handle = OpenProcess(InjectAccess, false, game.Pid);
        if (handle.IsInvalid) throw new IOException("无法连接游戏，请确认服务端与游戏权限一致");
        var loaded = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.FileName.Equals(native, StringComparison.OrdinalIgnoreCase));
        nint module;
        if (loaded != null) module = loaded.BaseAddress;
        else
        {
            // Resolve LoadLibraryW by its owning module RVA rather than assuming process base addresses match.
            nint localLoader = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            using var self = Process.GetCurrentProcess();
            var owner = self.Modules.Cast<ProcessModule>().First(m => localLoader >= m.BaseAddress && localLoader < m.BaseAddress + m.ModuleMemorySize);
            var remoteOwner = process.Modules.Cast<ProcessModule>().First(m => m.ModuleName.Equals(owner.ModuleName, StringComparison.OrdinalIgnoreCase));
            RunRemote(handle, remoteOwner.BaseAddress + (localLoader - owner.BaseAddress), Encoding.Unicode.GetBytes(native + "\0"));
            process.Refresh(); module = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.FileName.Equals(native, StringComparison.OrdinalIgnoreCase))?.BaseAddress ?? throw new IOException("游戏连接组件未加载");
        }
        nint local = LoadLibraryEx(native, 0, 1); if (local == 0) throw new IOException("原生连接组件无法读取");
        nint start;
        try { nint export = GetProcAddress(local, "TrialStart"); if (export == 0) throw new IOException("原生组件缺少连接入口"); start = module + (export - local); } finally { FreeLibrary(local); }
        var packet = new byte[512];
        BitConverter.TryWriteBytes(packet.AsSpan(0), 0x54524932u); BitConverter.TryWriteBytes(packet.AsSpan(4), profile.Timestamp); BitConverter.TryWriteBytes(packet.AsSpan(8), profile.ImageSize);
        Encoding.Unicode.GetBytes(pipeName + "\0").CopyTo(packet, 12);
        string[] names = ["PrintMessage", "Framework", "GetUIModule", "ProcessChat", "StringCtor", "StringSet", "StringDtor", "PlayerState", "ObjectManager", "ReceiveListing", "RequestCategoryListings", "GetAgentById", "RequestListingsUpdate"];
        for (int i = 0; i < names.Length; i++) BitConverter.TryWriteBytes(packet.AsSpan(396 + i * 4), profile.Addresses[names[i]]);
        Convert.FromBase64String(profile.PrintPrologue).AsSpan(0, 16).CopyTo(packet.AsSpan(448));
        Convert.FromBase64String(profile.ListingPrologue).AsSpan(0, 16).CopyTo(packet.AsSpan(464));
        BitConverter.TryWriteBytes(packet.AsSpan(480), 130u); BitConverter.TryWriteBytes(packet.AsSpan(484), 20u);
        BitConverter.TryWriteBytes(packet.AsSpan(488), 37u); BitConverter.TryWriteBytes(packet.AsSpan(492), 35u);
        string[] extra=["ExecuteMacro","OpenListing","AtkStage","GetAddonByName"];for(int i=0;i<extra.Length;i++)BitConverter.TryWriteBytes(packet.AsSpan(496+i*4),profile.Addresses[extra[i]]);
        uint result = RunRemote(handle, start, packet);
        if (result != 0) throw new IOException($"游戏连接初始化失败（{result}），未报告连接成功");
    }
    static uint RunRemote(SafeProcessHandle process, nint entry, byte[] bytes)
    {
        nint mem = VirtualAllocEx(process, 0, (nuint)bytes.Length, 0x3000, 4); if (mem == 0) throw new IOException("无法分配连接参数");
        nint thread = 0; bool finished = true;
        try
        {
            if (!WriteProcessMemory(process, mem, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length) throw new IOException("无法提交连接参数");
            thread = CreateRemoteThread(process, 0, 0, entry, mem, 0, out _); if (thread == 0) throw new IOException("游戏未接受连接组件");
            finished = WaitForSingleObject(thread, 10000) == 0;
            if (!finished) throw new TimeoutException("游戏连接超时；未释放仍可能被游戏读取的参数，不会自动重复注入");
            if (!GetExitCodeThread(thread, out var result)) throw new IOException("无法取得连接结果"); return result;
        }
        finally { if (thread != 0) CloseHandle(thread); if (finished) VirtualFreeEx(process, mem, 0, 0x8000); }
    }
    static string ExtractBridge()
    {
        var asm = typeof(NativeInjection).Assembly;
        string resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("TrialBridge.dll")) ?? throw new FileNotFoundException("缺少原生连接组件，当前构建不能连接游戏");
        using var stream = asm.GetManifestResourceStream(resource)!; using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        var data = bytes.ToArray(); string hash = Convert.ToHexString(SHA256.HashData(data));
        string folder = Path.Combine(Log.Root, "bridge", hash); Directory.CreateDirectory(folder); string path = Path.Combine(folder, "TrialBridge.dll");
        if (!File.Exists(path)) { string temp = path + ".tmp"; File.WriteAllBytes(temp, data); File.Move(temp, path, false); }
        if (!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(data))) throw new IOException("原生组件校验失败");
        return path;
    }
    public static object ReadParty(GameIdentity who)
    {
        if (!who.online) throw new IOException("请先登录角色");
        var profile = GameProfile.Load(); using var target = Process.GetProcessById(who.pid); using var handle = OpenProcess(ReadAccess, false, who.pid);
        if (handle.IsInvalid) throw new IOException("无法读取所选游戏的队伍数据");
        nint group = target.MainModule!.BaseAddress + checked((int)profile.Addresses["GroupManager"]) + 32;
        var data = Read(handle, group, 32752); int count = data[32732], leader = BitConverter.ToInt32(data, 32728);
        if (count > 8 || (count > 0 && (leader < 0 || leader >= 8))) throw new IOException("队伍结构不匹配");
        var members = new List<object>(); bool containsSelf = count == 0;
        for (int i = 0; i < count; i++)
        {
            int start = i * 1168; ulong cid = BitConverter.ToUInt64(data, start + 1016); if (cid == 0) continue;
            containsSelf |= cid.ToString() == who.contentId;
            int end = Array.IndexOf(data, (byte)0, start + 1052, 64); if (end < 0) end = start + 1116;
            string name = Encoding.UTF8.GetString(data, start + 1052, end - start - 1052);
            members.Add(new { id = cid.ToString(), name, world = GameNames.World(BitConverter.ToUInt16(data, start + 1050).ToString()), job = data[start + 1129], level = data[start + 1130], hp = BitConverter.ToUInt32(data, start + 1036), maxHp = BitConverter.ToUInt32(data, start + 1040), target=name+"@"+GameNames.World(BitConverter.ToUInt16(data,start+1050).ToString()),self = cid.ToString() == who.contentId, leader = i == leader });
        }
        if (!containsSelf) throw new IOException("队伍与当前角色不匹配，请刷新");
        return new { ok = true, partyId = BitConverter.ToInt64(data, 32712).ToString(), members, count,inParty=count>1,isLeader=leader>=0&&leader<count&&BitConverter.ToUInt64(data,leader*1168+1016).ToString()==who.contentId,confirmation="",status="" };
    }
}
