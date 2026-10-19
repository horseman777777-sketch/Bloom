using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Bloom.Core;
using Bloom.Native;
using Bloom.Ui;

namespace Bloom.App;

internal static partial class Captures
{
    /// `--qa-set docs`: the document window (Ui\DocWindow.cs) off-screen, dark and light: 보기, 편집, the bar for another
    /// program's change, a file that isn't UTF-8 (보기 전용), and the 메모 tool window beside the launcher (its look shared
    /// with the document window: it should not have changed). Then once on the screen for a moment, maximized as Windows
    /// does it (d6-maximized: its content, drawn like the others — no screen grab; no picture shows snapping). docs.txt:
    /// the saved file's bytes (CRLF and no BOM kept), what Windows' hit test answers on the maximize button, the window's
    /// styles, the maximized window against the work area.
    /// With a folder `docs-in` in the capture folder (copies of documents put there before the run), only how documents
    /// read is drawn instead: this set's own sample and each .md there in the document window, dark and light, at the top,
    /// one window further down and halfway through (s-dark-name-top, -next, -half …).
    private static async Task DocsScreens(Host host, Launcher w, string dir)
    {
        if (Directory.Exists(Path.Combine(dir, "docs-in")))
        {
            await DocSamples(dir);
            return;
        }
        var log = new StringBuilder();
        string src = Path.Combine(dir, "docs-src");
        Directory.CreateDirectory(src);
        string md = Path.Combine(src, "장보기 목록.md"), legacy = Path.Combine(src, "옛날 메모.md");
        File.WriteAllText(md, MarkdownNote.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(legacy, Encoding.GetEncoding(949).GetBytes("# 옛날 메모\r\n완성형(CP949)으로 저장된 글이에요.\r\n\r\n- 고칠 수 없고 보기만 돼요\r\n"));
        var note = Notes.New(MarkdownNote);
        w.SetPinned(true);
        DocWindow.OffScreenForQa = true;
        foreach (string theme in new[] { "dark", "light" })
        {
            Theme.Apply(theme);
            var d = DocWindow.Show(md);
            await Doc(d, dir, $"{theme}-d1-view");
            d.PaneForQa.InView = false;
            await Doc(d, dir, $"{theme}-d2-edit");
            if (theme == "dark")
            {
                // typed here and kept: the file keeps its CRLF and has no BOM
                d.TypeForQa(d.PaneForQa.Text.Replace("- [ ] 식빵", "- [ ] 식빵\n- [ ] 사과", StringComparison.Ordinal));
                await Task.Delay(1500);
                byte[] saved = File.ReadAllBytes(md);
                string text = Encoding.UTF8.GetString(saved);
                log.AppendLine($"autosave: {(text.Contains("- [ ] 사과\r\n", StringComparison.Ordinal) ? "saved" : "NOT saved")}, CRLF {(text.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n') ? "mixed" : "kept")}, BOM {(saved is [0xEF, 0xBB, 0xBF, ..] ? "added" : "none")}");
            }
            // written here, then by another program before it was saved: the bar asks
            d.TypeForQa(d.PaneForQa.Text + "\n메모: 영수증 챙기기");
            File.WriteAllText(md, "# 장보기\r\n다른 프로그램이 바꾼 글\r\n", new UTF8Encoding(false));
            d.OutsideForQa();
            d.PaneForQa.InView = true;
            await Doc(d, dir, $"{theme}-d3-conflict");
            if (theme == "dark")
            {
                // Bloom ending with no one to ask (signing out, --quit) while the bar asks: a copy beside the file
                DocWindow.RescueAll();
                var copies = Directory.GetFiles(src, "장보기 목록 (*).md");
                bool kept = copies.Length == 1 && File.ReadAllText(copies[0]).Contains("메모: 영수증 챙기기", StringComparison.Ordinal);
                log.AppendLine($"rescue: {copies.Length} copy {(kept ? "with the unsaved text" : "MISSING")} {(copies.Length == 1 ? Path.GetFileName(copies[0]) : "")} · the file still theirs {(File.ReadAllText(md).Contains("다른 프로그램이 바꾼 글", StringComparison.Ordinal) ? "yes" : "NO")}");
            }
            d.ReloadForQa();
            log.AppendLine($"{theme}: 다시 읽기 → \"{d.PaneForQa.Text.Split('\n')[1]}\"");
            File.WriteAllText(md, MarkdownNote.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
            d.OutsideForQa(); // nothing waits here: it comes in by itself
            log.AppendLine($"{theme}: outside change taken silently {(d.PaneForQa.Text == MarkdownNote ? "yes" : "NO")}");
            d.Close();
            var v = DocWindow.Show(legacy);
            await Doc(v, dir, $"{theme}-d4-viewonly");
            v.Close();
            // the 메모 tool window: the same head and glass as before the document window shared them
            w.Go(Mode.Root);
            host.Tools.Open("notes");
            ((NotesTool)host.Tools.Find("notes")!.Tool).Show(note.Id);
            await Group(host, w, dir, $"{theme}-d5-notes-tool");
            host.Tools.Close(host.Tools.Find("notes")!);
        }
        w.SetPinned(false);
        w.Dismiss();

        // once on the screen for a moment: maximized as Windows does it (its content drawn as before: nothing of the
        // screen is grabbed)
        DocWindow.OffScreenForQa = false;
        foreach (string theme in new[] { "dark", "light" })
        {
            Theme.Apply(theme);
            var m = DocWindow.Show(md);
            await Task.Delay(400);
            m.WindowState = WindowState.Maximized;
            await Task.Delay(500);
            Win32.GetWindowRect(m.Handle, out var r);
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            Win32.GetMonitorInfo(Win32.MonitorFromWindow(m.Handle, Win32.MONITOR_DEFAULTTONEAREST), ref info);
            var work = info.rcWork;
            log.AppendLine($"{theme} maximized: window {r.Left},{r.Top},{r.Right},{r.Bottom} · work area {work.Left},{work.Top},{work.Right},{work.Bottom} · frame margin {m.SpillForQa} · state {m.WindowState}");
            log.AppendLine($"hit test on the maximize button: {m.MaxHitForQa()} (HTMAXBUTTON = {Win32.HTMAXBUTTON})");
            long style = Win32.GetWindowLongPtr(m.Handle, -16).ToInt64(), ex = Win32.GetWindowLongPtr(m.Handle, Win32.GWL_EXSTYLE).ToInt64();
            log.AppendLine($"style 0x{style:X} (sysmenu {(style & 0x80000) != 0}, minimize box {(style & 0x20000) != 0}, maximize box {(style & 0x10000) != 0}, thick frame {(style & 0x40000) != 0}) · ex 0x{ex:X} (app window {(ex & Win32.WS_EX_APPWINDOW) != 0}, tool window {(ex & Win32.WS_EX_TOOLWINDOW) != 0})");
            await Doc(m, dir, $"{theme}-d6-maximized");
            m.WindowState = WindowState.Normal;
            await Task.Delay(300);
            log.AppendLine($"{theme} restored: frame margin {m.SpillForQa}");
            m.Close();
        }
        File.WriteAllText(Path.Combine(dir, "docs.txt"), log.ToString());
    }

    /// The documents in docs-in (and this set's sample) as they read: the top, one window down, halfway.
    private static async Task DocSamples(string dir)
    {
        string input = Path.Combine(dir, "docs-in");
        string sample = Path.Combine(input, "sample.md");
        File.WriteAllText(sample, MarkdownNote, new UTF8Encoding(false));
        DocWindow.OffScreenForQa = true;
        foreach (string theme in new[] { "dark", "light" })
        {
            Theme.Apply(theme);
            foreach (string file in Directory.GetFiles(input, "*.md"))
            {
                string name = $"s-{theme}-{Path.GetFileNameWithoutExtension(file)}";
                var d = DocWindow.Show(file);
                await Doc(d, dir, name + "-top");
                // the pane's outermost scroller (the first one found going down from it)
                await Task.Delay(300);
                if (Outermost<System.Windows.Controls.ScrollViewer>(d.PaneForQa) is { } scroll)
                {
                    scroll.ScrollToVerticalOffset(scroll.ViewportHeight);
                    await Doc(d, dir, name + "-next");
                    scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
                    await Doc(d, dir, name + "-half");
                }
                d.Close();
            }
        }
    }

    /// The first T under `root`, nearest first.
    private static T? Outermost<T>(DependencyObject root) where T : DependencyObject
    {
        var next = new System.Collections.Generic.Queue<DependencyObject>();
        next.Enqueue(root);
        while (next.Count > 0)
        {
            var at = next.Dequeue();
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(at); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(at, i);
                if (child is T found) return found;
                next.Enqueue(child);
            }
        }
        return null;
    }

    /// One document window's content as it is now, on its solid colour (no acrylic in a picture).
    private static async Task Doc(DocWindow d, string dir, string name)
    {
        d.FrameForQa.Background = Theme.P.Solid;
        await Task.Delay(450);
        await d.Dispatcher.InvokeAsync(() => Shots.Save(d.FrameForQa, Path.Combine(dir, name + ".png")), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
