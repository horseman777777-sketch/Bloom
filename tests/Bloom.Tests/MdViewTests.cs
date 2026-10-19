using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Threading;
using Bloom.Ui;

namespace Bloom.Tests;

/// The panes' Markdown view (Ui\MdView.cs, MdSurface): the space between blocks, selections over blocks and table
/// cells and what they copy, places kept over a redraw, hit-testing, Shift+arrows, screen readers.
internal static class MdViewTests
{
    public static void GapsAreTheLargerMargin()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show("# T\n\np\n\n## H\n\nq", 14, true, null);
            view.UpdateLayout();
            Check.Eq(string.Join(",", view.Surface.Gaps), "12,12,28,8", "the first block 12 below the top, then the larger of the two margins between neighbours");
        }
        finally
        {
            w.Close();
        }
    }

    public static void SelectionOverBlocksCopiesEachPart()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show("첫째 문단\n\n둘째\n\n셋째 문단", 14, true, null);
            view.UpdateLayout();
            Check.Eq(view.SelectedText(), "", "nothing selected: nothing to copy");
            view.SelectEverything();
            Check.Eq(view.SelectedText(), "첫째 문단\r\n둘째\r\n셋째 문단", "모두 선택: every block, a line break between");
            view.Surface.Select(new MdSpotAt(2, 0, 2), new MdSpotAt(0, 0, 3));
            Check.Eq(view.SelectedText(), "문단\r\n둘째\r\n셋째", "dragged up from the third block into the first: the end of one, all of the middle, the start of the last");
        }
        finally
        {
            w.Close();
        }
    }

    public static void TablesCopyByCells()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show("| a | b |\n|---|---|\n| 1 | 2 |\n| | 4 |\n\nafter", 14, true, null);
            view.UpdateLayout();
            view.SelectEverything();
            Check.Eq(view.SelectedText(), "a\tb\r\n1\t2\r\n\t4\r\nafter", "cells by tabs, rows and blocks by line breaks, an empty cell kept");
            view.Surface.Select(new MdSpotAt(0, 1, 0), new MdSpotAt(1, 0, 3));
            Check.Eq(view.SelectedText(), "b\r\n1\t2\r\n\t4\r\naft", "from a header cell into the paragraph after");
            view.Surface.Select(new MdSpotAt(0, 2, 0), new MdSpotAt(0, 3, 1));
            Check.Eq(view.SelectedText(), "1\t2", "two cells of a row");
        }
        finally
        {
            w.Close();
        }
    }

    public static void CopyKeepsCodeAndAddresses()
    {
        string url = "https://example.com/" + new string('a', 70);
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show($"값은 `Contoso.Runtime`에 {url} 끝\n\n- [x] 할 일\n\n```\nline one\n\tline two\n```", 14, true, null);
            view.UpdateLayout();
            view.SelectEverything();
            Check.Eq(view.SelectedText(), $"값은  Contoso.Runtime 에 {url} 끝\r\n할 일\r\nline one\r\n\tline two", "code with the spaces round it as chat copies, a shortened address whole, code's lines");
            var shown = view.Surface.Boxes[0].Parts[0].Text;
            int at = shown.Text.IndexOf('…');
            view.Surface.Select(new MdSpotAt(0, 0, at - 5), new MdSpotAt(0, 0, at + 1));
            Check.Eq(view.SelectedText(), shown.Text.Substring(at - 5, 6), "part of a shortened address: what is shown");
        }
        finally
        {
            w.Close();
        }
    }

    public static void ARedrawLeavesNothingSelected()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show(Paragraphs(5), 14, true, null);
            view.UpdateLayout();
            view.Surface.Select(new MdSpotAt(1, 0, 3), new MdSpotAt(3, 0, 5));
            Check.True(view.SelectedText().Length > 0, "something selected");
            view.Show(Paragraphs(5), 15, true, null); // another text size, a change from outside
            view.UpdateLayout();
            Check.Eq(view.SelectedText(), "", "nothing selected after a redraw: no copy of text never selected");
        }
        finally
        {
            w.Close();
        }
    }

    public static void ARedrawKeepsThePlace()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show(Paragraphs(60), 14, true, null);
            view.UpdateLayout();
            view.ScrollTo(view.ScrollableHeight * 0.3);
            view.UpdateLayout();
            var before = view.Place();
            Check.True(before.Block > 0, "scrolled into the text");
            view.Show(Paragraphs(60) + "\n\n추가", 14, true, null);
            view.UpdateLayout();
            view.GoTo(before);
            view.UpdateLayout();
            var after = view.Place();
            Check.Eq(after.Block, before.Block, "the same block at the top after a redraw");
            Check.True(Math.Abs(after.Into - before.Into) < 0.5, $"as far into it: {before.Into:0.0} → {after.Into:0.0} px");
            view.Show(Paragraphs(60), 17, true, null); // a text size step keeps the block
            view.UpdateLayout();
            Check.Eq(view.Place().Block, before.Block, "the same block at the top at another size");
            Pump(() =>
            {
                view.UpdateLayout();
                return view.Surface.Settled;
            }, "the rest shaped in idle slices");
            Check.Eq(view.Place().Block, before.Block, "and still once every block above is measured");
        }
        finally
        {
            w.Close();
        }
    }

    public static void HitTestRoundTrip()
    {
        var view = new MdView();
        var w = Hosted(view, 420, 2000);
        try
        {
            view.Show("# 제목 줄\n\n한국어 문장이 여러 줄로 이어집니다. 줄이 바뀌어도 낱말은 통째로 남아야 합니다. English words wrap at spaces too, and `Some.Code.Path` holds.\n\n- 목록 항목 하나\n- [ ] 체크 항목\n\n| 열 하나 | 열 둘 |\n|---|---|\n| 칸 안의 긴 글이 줄을 바꿉니다 | 짧음 |\n\n```\ncode line\n```", 14, true, null);
            view.UpdateLayout();
            var s = view.Surface;
            int checkedPlaces = 0;
            for (int k = 0; k < s.Boxes.Count; k++)
            {
                var b = s.Boxes[k];
                for (int p = 0; p < b.Parts.Count; p++)
                {
                    var part = b.Parts[p];
                    var g = Bloom.Core.LineBreaks.Graphemes(part.Text.Text);
                    for (int i = 0; i <= part.Text.Text.Length; i++)
                    {
                        if (!g[i]) continue;
                        int line = part.LineOf(i);
                        if (i > part.Starts[line] && i < part.Text.Text.Length && Bloom.Core.LineBreaks.IsSpace(part.Text.Text[i - 1]) && i == part.End(line)) continue; // hanging space: drawn past the line's end
                        var spot = new MdSpotAt(k, p, i);
                        var r = s.BoundsOf(spot);
                        var hit = s.HitForQa(new Point(r.X, r.Y + r.Height / 2));
                        Check.True(hit is { } h && h.Block == k && h.Part == p, $"place {k}/{p}/{i} hits its own text");
                        var back = s.BoundsOf(hit!.Value);
                        Check.True(Math.Abs(back.X - r.X) < 0.5 && Math.Abs(back.Y - r.Y) < 0.5, $"place {k}/{p}/{i}: bounds {r} → hit {hit.Value.At} → {back}");
                        checkedPlaces++;
                    }
                }
            }
            Check.True(checkedPlaces > 100, "places checked: " + checkedPlaces);
        }
        finally
        {
            w.Close();
        }
    }

    public static void ShiftArrowsExtendOverBlocks()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show("ab\n\ncd\n\nef", 14, true, null);
            view.UpdateLayout();
            var s = view.Surface;
            s.Select(new MdSpotAt(0, 0, 1), new MdSpotAt(0, 0, 1));
            s.Extend(Key.Right);
            s.Extend(Key.Right);
            s.Extend(Key.Right);
            Check.Eq(view.SelectedText(), "b\r\nc", "right over the end of a block into the next");
            s.Extend(Key.Down);
            Check.True(view.SelectedText().StartsWith("b\r\ncd\r\ne", StringComparison.Ordinal), "down a line into the third: " + view.SelectedText());
            s.Extend(Key.End);
            Check.Eq(view.SelectedText(), "b\r\ncd\r\nef", "to the end of its line");
            s.Extend(Key.Home);
            s.Extend(Key.Left);
            Check.Eq(view.SelectedText(), "b\r\ncd", "back to the end of the block before");
        }
        finally
        {
            w.Close();
        }
    }

    public static void ScreenReadersSeeTheText()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            string text = "# 제목\n\n본문과 [링크](https://example.com)\n\n- [ ] 할 일";
            int line = -1;
            view.Toggle = l =>
            {
                line = l;
                return true;
            };
            string? opened = null;
            view.OpenLink = u => opened = u;
            view.Show(text, 14, true, null);
            view.UpdateLayout();
            var peer = UIElementAutomationPeer.CreatePeerForElement(view.Surface);
            Check.Eq(peer.GetAutomationControlType(), AutomationControlType.Document, "a document");
            Check.Eq(peer.GetName(), "제목", "named by its first heading");
            var value = (IValueProvider)peer.GetPattern(PatternInterface.Value);
            Check.True(value.IsReadOnly && value.Value == "제목\r\n본문과 링크\r\n할 일", "its whole text, read-only: " + value.Value);
            var kids = peer.GetChildren();
            var link = kids.Single(k => k.GetAutomationControlType() == AutomationControlType.Hyperlink);
            Check.Eq(link.GetName(), "링크", "the link on screen, by its text");
            ((IInvokeProvider)link.GetPattern(PatternInterface.Invoke)).Invoke();
            Check.Eq(opened, "https://example.com", "invoked: opened");
            var box = kids.Single(k => k.GetAutomationControlType() == AutomationControlType.CheckBox);
            var toggle = (IToggleProvider)box.GetPattern(PatternInterface.Toggle);
            Check.Eq(toggle.ToggleState, ToggleState.Off, "the box unticked");
            toggle.Toggle();
            Check.True(line == 4 && toggle.ToggleState == ToggleState.On, "toggled: its line handed over, ticked");
        }
        finally
        {
            w.Close();
        }
    }

    /// `n` paragraphs of mixed length.
    private static string Paragraphs(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(string.Join(" ", Enumerable.Repeat($"문단{i} 글자", 1 + (i * 7 % 13) * 6))).Append("\n\n");
        return sb.ToString();
    }

    /// The view in a window off the screen (laid out as on it).
    internal static Window Hosted(FrameworkElement view, double width = 480, double height = 320)
    {
        var w = new Window { Content = view, Width = width, Height = height, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        System.Windows.Media.TextOptions.SetTextFormattingMode(w, System.Windows.Media.TextFormattingMode.Display);
        w.Show();
        return w;
    }

    /// The dispatcher run (idle slices, pictures read off the UI thread) until `done`, for at most 10 s.
    internal static void Pump(Func<bool> done, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("timed out: " + what);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(5);
        }
    }
}
