using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NB.Core.IO;

/// <summary>
/// Background test runs (env <c>NB_STUDIO_BACKGROUND=1</c>, set by test harnesses): start programs without taking the
/// keyboard focus or coming to the front, so a person working on the same desktop keeps typing undisturbed. Windows
/// started this way open behind the others (SW_SHOWNOACTIVATE in the start-up info; the program's first ShowWindow
/// uses it) and are pushed to the bottom of the window order. They still render, so PrintWindow captures them.
/// </summary>
public static class QuietLaunch
{
    /// <summary>True when this process runs for a test harness that asked for windows to stay in the background.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("NB_STUDIO_BACKGROUND") is "1" or "true";

    const int SW_SHOWNOACTIVATE = 4;
    const uint STARTF_USESHOWWINDOW = 1, CREATE_UNICODE_ENVIRONMENT = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, string? env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    /// <summary>
    /// Starts <paramref name="psi"/> (file, ArgumentList or Arguments, WorkingDirectory, Environment) with its window
    /// shown without activation, then keeps its windows at the bottom of the window order for a while in the background.
    /// </summary>
    public static Process Start(ProcessStartInfo psi, int keepBehindSeconds = 60)
    {
        var cmd = new StringBuilder(Quote(psi.FileName));
        if (psi.ArgumentList.Count > 0) foreach (var a in psi.ArgumentList) cmd.Append(' ').Append(Quote(a));
        else if (!string.IsNullOrEmpty(psi.Arguments)) cmd.Append(' ').Append(psi.Arguments);
        var env = new StringBuilder();
        foreach (var kv in psi.Environment.Where(kv => kv.Value != null).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            env.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        env.Append('\0');
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = STARTF_USESHOWWINDOW, wShowWindow = SW_SHOWNOACTIVATE };
        string? dir = string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory;
        if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, env.ToString(), dir, ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {psi.FileName}");
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        var p = Process.GetProcessById(pi.dwProcessId);
        KeepBehind(p, keepBehindSeconds);
        return p;
    }

    /// <summary>Keeps the windows of <paramref name="p"/> at the bottom of the window order for a while (in the background).</summary>
    public static void KeepBehind(Process p, int keepBehindSeconds = 60)
    {
        int pid = p.Id;
        _ = Task.Run(async () =>
        {
            var end = DateTime.UtcNow.AddSeconds(keepBehindSeconds);
            while (DateTime.UtcNow < end)
            {
                try { if (p.HasExited) return; } catch (Exception) { return; }
                PushBehind(pid);
                await Task.Delay(250);
            }
        });
    }

    [DllImport("user32")] static extern bool LockSetForegroundWindow(uint code);

    /// <summary>
    /// Stops programmatic foreground changes (SetForegroundWindow, as GLFW does when it shows a GL window) while this
    /// background run starts. Windows lifts the lock by itself as soon as the user clicks a window or presses Alt.
    /// </summary>
    public static bool LockForeground() => LockSetForegroundWindow(1 /* LSFW_LOCK */);

    /// <summary>Moves every visible top-level window of process <paramref name="pid"/> to the bottom, without activating it.</summary>
    public static void PushBehind(int pid)
    {
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out var wp) != 0 && wp == pid) PushBehind(h);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Moves <paramref name="hwnd"/> to the bottom of the window order without activating, moving or resizing it.</summary>
    public static void PushBehind(IntPtr hwnd)
    {
        const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        SetWindowPos(hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (var c in a)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            sb.Append('\\', bs).Append(c); bs = 0;
        }
        sb.Append('\\', bs * 2).Append('"');
        return sb.ToString();
    }
}
