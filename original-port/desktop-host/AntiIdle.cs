using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NativeDesktop;

// Desktop-only activity pulse. No Dalamud, ACT, renderer or process memory writes.
// Windows message approach independently implemented; reference research recorded in SOURCES.md.
internal sealed class AntiIdle : IDisposable
{
    internal sealed record Target(int Pid, long StartedUtcTicks, nint Window, string CharacterId);
    readonly Func<Target?> selected;
    readonly Func<Target, bool> identityStillMatches;
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    volatile bool enabled;
    long next;
    public string Status { get; private set; } = "防掉线已关闭";
    public bool Enabled { get => enabled; set { enabled = value; Interlocked.Exchange(ref next, 0); if (!value) Status = "防掉线已关闭"; } }
    public AntiIdle(Func<Target?> selected, Func<Target, bool> identityStillMatches)
    {
        this.selected = selected; this.identityStillMatches = identityStillMatches;
        worker = Task.Run(Loop);
    }
    internal static bool Due(bool enabled, bool online, bool foreground, uint idleMilliseconds, long now, long next)
        => enabled && online && now >= next && (!foreground || idleMilliseconds >= 60000);
    async Task Loop()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                if (!enabled) continue;
                var target = selected();
                if (target == null || target.CharacterId.Length == 0) { Status = "等待所选角色登录"; continue; }
                long now = Environment.TickCount64;
                var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
                if (!GetLastInputInfo(ref last)) { Status = "无法读取空闲状态"; continue; }
                uint idle = unchecked((uint)Environment.TickCount - last.Time);
                if (!Due(enabled, true, GetForegroundWindow() == target.Window, idle, now, Interlocked.Read(ref next))) continue;
                if (!VerifyWindow(target) || !identityStillMatches(target)) { Status = "角色或窗口已变化，已跳过"; continue; }
                // Do not release a modifier the player is physically holding.
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) continue;
                if (!enabled || selected() != target) continue;
                bool pressed = SendMessageTimeout(target.Window, 0x100, 0xA2, 1 | (0x1D << 16), 2, 150, out _) != 0;
                try { if (pressed) await Task.Delay(80, CancellationToken.None); }
                finally
                {
                    // Pair the key-up even if disabled while the key-down was pending.
                    // The exact HWND and process creation time must still match.
                    if (VerifyWindow(target)) SendMessageTimeout(target.Window, 0x101, 0xA2, unchecked((nint)0xC01D0001u), 2, 150, out _);
                }
                Interlocked.Exchange(ref next, Environment.TickCount64 + 120000);
                Status = pressed ? "防掉线运行中 · " + DateTime.Now.ToString("HH:mm:ss") : "游戏窗口未响应，稍后重试";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = "防掉线已停止：" + e.Message; enabled = false; }
    }
    static bool VerifyWindow(Target target)
    {
        if (target.Window == 0 || !IsWindow(target.Window)) return false;
        GetWindowThreadProcessId(target.Window, out uint pid); if (pid != target.Pid) return false;
        var name = new StringBuilder(64); if (GetClassName(target.Window, name, name.Capacity) == 0 || name.ToString() != "FFXIVGAME") return false;
        try { using var process = Process.GetProcessById(target.Pid); return !process.HasExited && process.ProcessName == "ffxiv_dx11" && process.StartTime.ToUniversalTime().Ticks == target.StartedUtcTicks; }
        catch { return false; }
    }
    public void Dispose() { enabled = false; stop.Cancel(); try { worker.Wait(TimeSpan.FromSeconds(1)); } catch { } /* worker owns cancellation lifetime until exit */ }
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Time; }
    [DllImport("user32")] static extern bool GetLastInputInfo(ref LastInput value);
    [DllImport("user32")] static extern nint GetForegroundWindow();
    [DllImport("user32")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] static extern bool IsWindow(nint window);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32", SetLastError = true)] static extern nint SendMessageTimeout(nint window, uint message, nuint key, nint flags, uint options, uint timeout, out nuint result);
}
