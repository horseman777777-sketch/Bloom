using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;
using Bloom.Core;

namespace Bloom.Tests;

internal static class StallWatchTests
{
    public static void BlockedUiThreadIsLogged()
    {
        var lines = Watch(d =>
        {
            d.BeginInvoke(() => Thread.Sleep(1200));
            return 1;
        });
        var m = Regex.Match(lines.Single(), @"^ui stall (\d+) ms$");
        Check.True(m.Success, "log line names the stall: " + lines.Single());
        long ms = long.Parse(m.Groups[1].Value);
        Check.True(ms is >= 1000 and < 2000, $"measured stall {ms} ms is about the 1.2 s block");
    }

    public static void ShortBusyMomentsAreNotRecorded()
    {
        var lines = Watch(d =>
        {
            d.BeginInvoke(() => Thread.Sleep(200));
            return 0;
        });
        Check.Eq(lines.Count, 0, "a 0.2 s busy moment is not a stall");
    }

    public static void EveryStallIsLogged()
    {
        var lines = Watch(d =>
        {
            d.BeginInvoke(() => Thread.Sleep(1000));
            Thread.Sleep(1600); // the first stall answered and logged
            d.BeginInvoke(() => Thread.Sleep(1000));
            return 2;
        });
        Check.Eq(lines.Count, 2, "both stalls are logged");
        Check.True(lines.All(l => Regex.IsMatch(l, @"^ui stall \d+ ms$")), "each a plain line: " + string.Join(" | ", lines));
    }

    public static void OldDumpsGoWithTheirFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bloom-stall-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stall-20261019-034734.dmp"), "old");
        StallWatch.RemoveOldDumps(dir);
        Check.True(!Directory.Exists(dir), "the folder and its dumps are gone");
        StallWatch.RemoveOldDumps(dir); // no folder: nothing to do, nothing thrown
    }

    /// A watch over a fresh UI dispatcher; `act` blocks it as a test needs and returns how many log lines to wait for.
    private static System.Collections.Generic.List<string> Watch(Func<Dispatcher, int> act)
    {
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        var lines = new ConcurrentQueue<string>();
        using (new StallWatch(d!, lines.Enqueue))
        {
            Thread.Sleep(300); // quick answers first
            int expected = act(d!);
            WaitUntil(() => lines.Count >= expected, 3000);
            Thread.Sleep(300); // nothing more comes
        }
        d!.InvokeShutdown();
        return lines.ToList();
    }

    private static void WaitUntil(Func<bool> done, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < ms) Thread.Sleep(20);
    }
}
