using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Bloom.Core;
using Bloom.Native;
using Bloom.WinKey;

namespace Bloom.App;

/// 관리자 앱 위에서도 Win 키 (issue #3). Bloom's own hook (WinKeyHook) runs at medium integrity and is blind while an
/// elevated window has focus. Switched on, WinKeyHelper.exe — copied to Program Files and registered as this user's
/// task with the full admin token by its own `--install` (one UAC prompt) — takes the lone Win press there and sets
/// the Fire event. Bloom makes both events, starts the task with its process id and ends it with the Stop event; the
/// helper also ends with Bloom. Offered only to an administrator account's limited token, never to a dev copy.
internal sealed class ElevatedWinKey : IDisposable
{
    /// The installed copy for this user: none, this protocol, another protocol (to install again), or unknown
    /// (Task Scheduler didn't answer).
    public enum Copy { None, Ready, Other, Unknown }

    public enum Elevated { Done, Declined, Failed }

    private static readonly Lazy<bool> offered = new(() => !Paths.IsDev && Paths.InstanceId == "Bloom" && Win32.LimitedAdminToken());

    public static bool Offered => offered.Value;

    private static string Installed => Path.Combine(WinKeyProtocol.InstallDir, WinKeyProtocol.ExeName);

    private static string Sid()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.User!.Value;
    }

    public static Copy Check()
    {
        try
        {
            if (!File.Exists(Installed)) return Copy.None;
            using (var ts = TaskService.Connect())
                if (!ts.Exists(Sid())) return Copy.None;
            return FileVersionInfo.GetVersionInfo(Installed).FileMajorPart.ToString(CultureInfo.InvariantCulture) == WinKeyProtocol.Version ? Copy.Ready : Copy.Other;
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            Log.Error("win-key helper", e);
            return Copy.Unknown;
        }
    }

    private readonly EventWaitHandle fire, stop;
    private readonly ManualResetEvent quit = new(false);

    private ElevatedWinKey(Action fired)
    {
        fire = new EventWaitHandle(false, EventResetMode.AutoReset, WinKeyProtocol.FireEvent);
        stop = new EventWaitHandle(false, EventResetMode.ManualReset, WinKeyProtocol.StopEvent);
        stop.Reset(); // still set when a helper of an earlier start held it open
        var fireOn = fire;
        var quitOn = quit;
        new Thread(() =>
        {
            try
            {
                while (WaitHandle.WaitAny(new WaitHandle[] { fireOn, quitOn }) == 0) fired();
            }
            finally
            {
                fireOn.Dispose();
                quitOn.Dispose();
            }
        })
        { IsBackground = true, Name = "win-key admin" }.Start();
    }

    /// The helper started for this Bloom, `fired` on each tap it takes (null: no task, or it couldn't start).
    public static ElevatedWinKey? Start(Action fired)
    {
        ElevatedWinKey? helper = null;
        try
        {
            helper = new ElevatedWinKey(fired);
            using var me = Process.GetCurrentProcess();
            using var ts = TaskService.Connect();
            if (ts.Run(Sid(), Environment.ProcessId.ToString(CultureInfo.InvariantCulture), me.SessionId))
            {
                Log.Write("win-key helper started");
                return helper;
            }
            Log.Write("win-key helper: no task");
        }
        catch (Exception e) when (e is COMException or WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            Log.Error("win-key helper", e);
        }
        helper?.Dispose();
        return null;
    }

    /// The helper leaves (Stop); Bloom's own hook stays.
    public void Dispose()
    {
        stop.Set();
        stop.Dispose();
        quit.Set();
    }

    /// `WinKeyHelper.exe --install` / `--remove` from Bloom's folder with the UAC prompt, waited for to the end. On a
    /// thread of its own: the prompt waits for a person.
    public static Task<Elevated> RunElevated(string arg)
    {
        var done = new TaskCompletionSource<Elevated>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => done.SetResult(Elevate(arg))) { IsBackground = false, Name = "win-key elevate" };
        thread.SetApartmentState(ApartmentState.STA); // ShellExecuteEx may hand the start to COM shell extensions
        thread.Start();
        return done.Task;
    }

    private static Elevated Elevate(string arg)
    {
        var info = new Win32.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<Win32.SHELLEXECUTEINFO>(),
            fMask = Win32.SEE_MASK_NOCLOSEPROCESS | Win32.SEE_MASK_FLAG_NO_UI | Win32.SEE_MASK_NOASYNC,
            lpVerb = "runas",
            lpFile = Path.Combine(Paths.AppDir, WinKeyProtocol.ExeName),
            lpParameters = arg,
            lpDirectory = Environment.SystemDirectory,
        };
        if (!Win32.ShellExecuteEx(ref info))
        {
            int error = Marshal.GetLastWin32Error();
            Log.Write($"win-key helper {arg}: not started ({error})");
            return error == 1223 /* ERROR_CANCELLED: UAC declined */ ? Elevated.Declined : Elevated.Failed;
        }
        try
        {
            Win32.WaitForSingleObject(info.hProcess, 60_000);
            bool ok = Win32.GetExitCodeProcess(info.hProcess, out uint code) && code == 0;
            Log.Write($"win-key helper {arg}: exit {code}");
            return ok ? Elevated.Done : Elevated.Failed;
        }
        finally
        {
            Win32.CloseHandle(info.hProcess);
        }
    }
}
