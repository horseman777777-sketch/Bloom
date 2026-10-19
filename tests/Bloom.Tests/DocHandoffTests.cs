using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bloom.App;

namespace Bloom.Tests;

/// `Bloom.exe --open <file>` handing a file to the running Bloom (App\DocHandoff.cs), over a pipe of the test's own.
internal static class DocHandoffTests
{
    public static void PathGoesOverAndTheAnswerComesBack()
    {
        string pipe = "bloom-test-open-" + Guid.NewGuid().ToString("N")[..12];
        string file = Path.Combine(Path.GetTempPath(), "bloom-handoff-" + Guid.NewGuid().ToString("N")[..8] + " 한글 이름.md");
        File.WriteAllText(file, "# hi\n");
        try
        {
            var got = new System.Collections.Concurrent.ConcurrentQueue<string>();
            DocHandoff.Listen(pipe, path =>
            {
                got.Enqueue(path);
                return Task.FromResult(true);
            });
            DocHandoff.Answer? sent = null;
            for (int i = 0; i < 40 && sent is null; i++)
            {
                sent = DocHandoff.Send(pipe, file);
                if (sent is null) Thread.Sleep(50); // the listener's first pipe is still being made
            }
            Check.Eq(sent, DocHandoff.Answer.Opened, "the running Bloom opened it");
            Check.True(got.TryDequeue(out string? first) && first == file, "the full path, spaces and Korean as they were");
            Check.Eq(DocHandoff.Send(pipe, file), DocHandoff.Answer.Opened, "again (the same file: its window comes forward)");
            Check.Eq(DocHandoff.Send(pipe, "relative.md"), DocHandoff.Answer.Refused, "not a full path: refused");
            Check.Eq(DocHandoff.Send(pipe, file + ".gone.md"), DocHandoff.Answer.Refused, "a file that isn't there: refused");
            Check.Eq(got.Count, 1, "only the acceptable one reached the opening");

            // a Bloom that takes too long to answer: Silent (the start then says so in a message box)
            string slow = "bloom-test-slow-" + Guid.NewGuid().ToString("N")[..12];
            DocHandoff.Listen(slow, async _ =>
            {
                await Task.Delay(3000);
                return true;
            });
            DocHandoff.Answer? late = null;
            for (int i = 0; i < 40 && late is null; i++)
            {
                late = DocHandoff.Send(slow, file, answerMs: 300);
                if (late is null) Thread.Sleep(50);
            }
            Check.Eq(late, DocHandoff.Answer.Silent, "no answer in time: silent, not opened");
        }
        finally
        {
            File.Delete(file);
        }
        var clock = Stopwatch.StartNew();
        Check.Eq(DocHandoff.Send("bloom-test-none-" + Guid.NewGuid().ToString("N")[..12], file), null, "no Bloom: no answer, so the start becomes the Bloom");
        Check.True(clock.ElapsedMilliseconds < 500, $"and no wait for one ({clock.ElapsedMilliseconds} ms)");
    }

    public static void AcceptsOnlyFullPathsOfFiles()
    {
        Check.True(!DocHandoff.Acceptable(""), "nothing");
        Check.True(!DocHandoff.Acceptable(@"docs\a.md"), "relative");
        Check.True(!DocHandoff.Acceptable(Path.GetTempPath()), "a folder");
        Check.True(!DocHandoff.Acceptable(@"C:\no\such\file-" + Guid.NewGuid().ToString("N") + ".md"), "missing");
    }
}
