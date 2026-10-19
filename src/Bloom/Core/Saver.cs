using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Bloom.Core;

/// Debounces snapshots and serializes their commits. UI-owned data is captured on its dispatcher;
/// debounced file IO stays on a worker, and a stale queued snapshot cannot overwrite a newer explicit flush.
internal sealed class Saver : IDisposable
{
    private readonly object stateGate = new();
    private readonly object writeGate = new();
    private readonly Func<Action> prepare;
    private readonly Dispatcher? owner;
    private readonly Timer timer;
    private readonly int delay;
    private long revision, due;
    private bool pending, disposed;

    /// For stores that already synchronize access to their data (clipboard, query memory, app names).
    public Saver(Action save, int delayMs = 400) : this(() => save, null, delayMs) { }

    private Saver(Func<Action> prepare, Dispatcher? owner, int delayMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs);
        this.prepare = prepare;
        this.owner = owner;
        delay = delayMs;
        timer = new Timer(_ => Elapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// Call while loading the store on its owning UI thread. Only the immutable JSON goes to a worker.
    public static Saver ForJson<T>(string file, Func<T> value, int delayMs = 400) => new(() =>
    {
        string snapshot = JsonSerializer.Serialize(value(), Json.Options);
        return () => Json.SaveText(file, snapshot);
    }, Dispatcher.CurrentDispatcher, delayMs);

    /// For data a UI holds itself (a document's text): `prepare` runs on this thread's dispatcher and takes the snapshot,
    /// the write it hands back runs on a worker (on the caller's thread for Flush).
    public static Saver ForUi(Func<Action> prepare, int delayMs) => new(prepare, Dispatcher.CurrentDispatcher, delayMs);

    public void Touch()
    {
        lock (stateGate)
        {
            if (disposed) return;
            revision++;
            pending = true;
            due = Environment.TickCount64 + delay;
            timer.Change(delay, Timeout.Infinite);
        }
    }

    private void Elapsed()
    {
        long version;
        lock (stateGate)
        {
            if (disposed || !pending) return;
            long remaining = due - Environment.TickCount64;
            if (remaining > 0)
            {
                timer.Change((int)Math.Min(remaining, int.MaxValue), Timeout.Infinite);
                return;
            }
            version = revision;
        }
        if (owner is null) Capture(version, background: false);
        else if (!owner.HasShutdownStarted) owner.BeginInvoke(DispatcherPriority.Background, () => Capture(version, background: true));
    }

    /// Explicit flush also saves values changed without Touch, as export/shutdown callers expect.
    public void Flush()
    {
        long version;
        lock (stateGate)
        {
            if (disposed) return;
            version = ++revision;
            pending = true;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (owner is not null && !owner.CheckAccess()) owner.Invoke(() => Capture(version, background: false));
        else Capture(version, background: false);
    }

    private void Capture(long version, bool background)
    {
        try
        {
            Action commit;
            lock (stateGate)
            {
                if (disposed || !pending || version != revision) return;
                commit = prepare();
            }
            if (background) _ = Task.Run(() => Write(version, commit));
            else Write(version, commit);
        }
        catch (Exception e)
        {
            Log.Error("saver snapshot", e);
        }
    }

    private void Write(long version, Action commit)
    {
        lock (writeGate)
        {
            lock (stateGate)
                if (disposed || !pending || version != revision) return;
            try
            {
                commit();
            }
            catch (Exception e)
            {
                Log.Error("saver", e);
            }
            finally
            {
                lock (stateGate)
                    if (version == revision) pending = false;
            }
        }
    }

    /// Cancel queued work and finish an active commit before a store is reloaded/replaced.
    public void Dispose()
    {
        lock (stateGate)
        {
            disposed = true;
            pending = false;
            revision++;
            timer.Dispose();
        }
        lock (writeGate) { }
    }
}
