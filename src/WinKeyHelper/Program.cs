using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

[assembly: AssemblyFileVersion(Bloom.WinKey.WinKeyProtocol.FileVersion)]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Bloom.WinKey;

/// WinKeyHelper.exe (issue #3): Bloom's Win key over apps run as administrator. Bloom's own hook runs at medium
/// integrity and Windows keeps it blind while an elevated window has focus; this copy runs elevated (a task Bloom
/// starts) and does only that: a lone Win press → the Start menu stays closed and Bloom is told.
///   WinKeyHelper.exe <Bloom's pid>   serve that Bloom until it ends or sets the Stop event
///   WinKeyHelper.exe --install       (elevated) copy to Program Files, register this user's task, add the Apps entry
///   WinKeyHelper.exe --remove        (elevated) this user's task gone; the copy and the entry too when no task is left
/// No settings, no log, no user folders. Exit code 0 = done.
internal static unsafe class Program
{
    private const uint WM_APP_FIRE = 0x8000 + 1, WM_QUIT = 0x12, PM_REMOVE = 1, QS_ALLINPUT = 0x4FF, MWMO_INPUTAVAILABLE = 4, INFINITE = 0xFFFFFFFF;

    private static readonly WinTap tap = new();
    private static IntPtr bloom, fire, hook;
    private static int bloomPid;
    private static uint threadId;

    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        try
        {
            return args[0] switch { "--install" => Install(), "--remove" => Remove(), _ => Serve(args[0]) };
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static string Sid()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.User!.Value;
    }

    private static int Install()
    {
        string dir = WinKeyProtocol.InstallDir, exe = Path.Combine(dir, WinKeyProtocol.ExeName), self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), exe, StringComparison.OrdinalIgnoreCase))
        {
            Clear(dir, keepDir: true);
            File.Copy(self, exe, true); // a copy: it gets the folder's admin-only access, the original stays Bloom's
        }
        string sid = Sid();
        using (var ts = TaskService.Connect()) ts.Register(sid, WinKeyProtocol.TaskXml(sid, exe, dir));
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(WinKeyProtocol.UninstallKey);
        key.SetValue("DisplayName", WinKeyProtocol.DisplayName);
        key.SetValue("DisplayVersion", WinKeyProtocol.FileVersion);
        key.SetValue("Publisher", "Hyeon");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", $"\"{exe}\" --remove");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return 0;
    }

    private static int Remove()
    {
        bool last;
        using (var ts = TaskService.Connect()) last = ts.Delete(Sid());
        if (!last) return 0; // another user's helper still runs from the copy
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) hklm.DeleteSubKeyTree(WinKeyProtocol.UninstallKey, false);
        Clear(WinKeyProtocol.InstallDir, keepDir: false);
        string parent = Path.GetDirectoryName(WinKeyProtocol.InstallDir)!;
        if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent);
        return 0;
    }

    /// The folder emptied (and gone unless `keepDir`). A file still running (this copy, started by Windows' Apps
    /// list, or a helper not ended yet) is renamed out of the way and deleted at the next restart.
    private static void Clear(string dir, bool keepDir)
    {
        if (Directory.Exists(dir))
            foreach (string f in Directory.GetFiles(dir))
                try
                {
                    File.Delete(f);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    string old = f + "." + Guid.NewGuid().ToString("N") + ".old";
                    File.Move(f, old);
                    MoveFileEx(old, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
                }
        if (keepDir) Directory.CreateDirectory(dir);
        else if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
        else if (Directory.Exists(dir)) MoveFileEx(dir, null, 4);
    }

    /// Serves the Bloom with this process id: only a running Bloom.exe, only while it runs.
    private static int Serve(string arg)
    {
        if (WinKeyProtocol.ParsePid(arg) is not { } pid) return 2;
        bloomPid = pid;
        bloom = OpenProcess(0x00100000 /* SYNCHRONIZE */ | 0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (bloom == IntPtr.Zero || !WinKeyProtocol.IsBloomImage(ImagePath(bloom))) return 2;
        fire = OpenEvent(0x0002 /* EVENT_MODIFY_STATE */, false, WinKeyProtocol.FireEvent);
        IntPtr stop = OpenEvent(0x00100000 /* SYNCHRONIZE */, false, WinKeyProtocol.StopEvent);
        if (fire == IntPtr.Zero || stop == IntPtr.Zero) return 2; // Bloom makes both before it starts the helper
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, &OnKey, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) return 1;
        IntPtr* wait = stackalloc IntPtr[] { bloom, stop };
        // Bloom ended or said stop (0, 1), or a message: the hook's calls and the taps it posted
        while (MsgWaitForMultipleObjectsEx(2, wait, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == 2)
        {
            MSG msg;
            while (PeekMessage(&msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) goto done;
                if (msg.message == WM_APP_FIRE) Fire((byte)msg.wParam);
            }
        }
    done:
        UnhookWindowsHookEx(hook);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            int m = (int)wParam;
            bool down = m is 0x100 or 0x104, up = m is 0x101 or 0x105; // WM_(SYS)KEYDOWN / UP
            // a tap is taken only while Bloom still runs: never a swallowed Win key without it
            if (tap.Key(k->vkCode, down, up, k->dwExtraInfo == WinKeyProtocol.OwnTag, ModifiersHeld) && WaitForSingleObject(bloom, 0) != 0)
            {
                PostThreadMessage(threadId, WM_APP_FIRE, (IntPtr)k->vkCode, IntPtr.Zero);
                return 1;
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    private static bool ModifiersHeld() =>
        (GetAsyncKeyState((int)WinTap.VK_SHIFT) & 0x8000) != 0 || (GetAsyncKeyState((int)WinTap.VK_CONTROL) & 0x8000) != 0 || (GetAsyncKeyState((int)WinTap.VK_MENU) & 0x8000) != 0;

    /// The swallowed release replayed behind the mask key (the Start menu stays closed), then Bloom may take the
    /// foreground (this process sent the last input) and is told.
    private static void Fire(byte winVk)
    {
        keybd_event((byte)WinTap.VK_MASK, 0, 0, WinKeyProtocol.OwnTag);
        keybd_event((byte)WinTap.VK_MASK, 0, 2 /* KEYEVENTF_KEYUP */, WinKeyProtocol.OwnTag);
        keybd_event(winVk, 0, 2, WinKeyProtocol.OwnTag);
        AllowSetForegroundWindow(bloomPid);
        SetEvent(fire);
    }

    private static string? ImagePath(IntPtr process)
    {
        char* buf = stackalloc char[1024];
        int len = 1024;
        return QueryFullProcessImageNameW(process, 0, buf, &len) ? new string(buf, 0, len) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, delegate* unmanaged<int, IntPtr, IntPtr, IntPtr> fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr* handles, uint ms, uint wake, uint flags);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(MSG* msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, int* size);
    [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode)] private static extern IntPtr OpenEvent(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode)] private static extern bool MoveFileEx(string from, string? to, uint flags);
}
