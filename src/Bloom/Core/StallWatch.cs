using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Bloom.Native;

namespace Bloom.Core;

/// Records the moments the UI thread stops answering. A watcher thread posts a no-op at Input priority (where typed
/// keys wait) and times it on the unbiased clock (the PC's sleep doesn't count); a no-op that waited `Noted` or longer
/// goes to the log with how long. Until 0.14 a long stall also got a dump (the freezes they were for went with the
/// capture window's software drawing), written by the runtime's createdump, whose console window flashed up each time.
internal sealed class StallWatch : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan Noted = TimeSpan.FromMilliseconds(500);

    private readonly Dispatcher ui;
    private readonly Action<string> log;
    private readonly Thread thread;
    private volatile bool stopped;
    private long sent = -1; // when the waiting no-op went out (Now); -1: none out
    private long answered = -1; // when it ran

    public StallWatch(Dispatcher ui, Action<string> log)
    {
        this.ui = ui;
        this.log = log;
        thread = new Thread(Run) { IsBackground = true, Name = "stall watch", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// The dumps older versions kept (Local\stalls): the folder goes, with what is in it.
    public static void RemoveOldDumps(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("old stall dumps: " + e.Message);
        }
    }

    /// Milliseconds on the unbiased interrupt clock: it stands still while the PC sleeps or hibernates.
    private static long Now()
    {
        _ = Win32.QueryUnbiasedInterruptTime(out ulong t);
        return (long)(t / 10_000);
    }

    private void Run()
    {
        while (true)
        {
            Thread.Sleep(Every);
            if (stopped || ui.HasShutdownStarted) return; // quitting: its saving may hold the UI thread on purpose
            long s = Interlocked.Read(ref sent), a = Interlocked.Read(ref answered);
            if (s >= 0 && a >= 0)
            {
                long waited = a - s;
                if (waited >= Noted.TotalMilliseconds) log($"ui stall {waited} ms");
                s = -1;
            }
            if (s < 0)
            {
                Interlocked.Exchange(ref answered, -1);
                Interlocked.Exchange(ref sent, Now());
                ui.BeginInvoke(DispatcherPriority.Input, Answer);
            }
        }
    }

    private void Answer() => Interlocked.Exchange(ref answered, Now());

    public void Dispose() => stopped = true;
}
