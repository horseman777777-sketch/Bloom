using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using Bloom.Ui;

namespace Bloom.Tests;

/// The pane's view of blocks laid out one by one (Ui\MdView.cs): the space between them and a selection over several.
internal static class MdViewTests
{
    public static void GapsAreTheLargerMargin()
    {
        var view = new MdView();
        view.Show([P("가", 6, 8), P("나", 20, 4), P("다", 0, 8)], 14);
        Check.Eq(string.Join(",", view.Gaps), "12,20,4", "the first block 12 below the top, then the larger of the two margins between neighbours");
    }

    public static void SelectionOverBlocksCopiesEachPart()
    {
        var view = new MdView();
        var first = P("첫째 문단", 0, 8);
        var last = P("셋째 문단", 0, 8);
        view.Show([first, P("둘째", 0, 8), last], 14);
        Check.Eq(view.SelectedText(), "", "nothing selected: nothing to copy");
        view.SelectEverything();
        Check.Eq(view.SelectedText(), "첫째 문단\r\n둘째\r\n셋째 문단", "모두 선택: every block, a line break between");
        view.Select(2, Offset(last, 2), 0, Offset(first, 3));
        Check.Eq(view.SelectedText(), "문단\r\n둘째\r\n셋째", "dragged up from the third block into the first: the end of one, all of the middle, the start of the last");
    }

    public static void ARedrawLeavesNothingSelected()
    {
        var view = new MdView();
        var w = Hosted(view);
        try
        {
            view.Show(Paragraphs(5), 14);
            view.UpdateLayout();
            view.Select(1, 3, 3, 5);
            view.Show(Paragraphs(5), 14); // another text size, a change from outside
            view.UpdateLayout();
            Check.Eq(view.SelectedText(), "", "nothing selected after a redraw: no copy of text never selected");
            Check.Eq(view.PaintedForQa().Replace("·", "").Replace("|", "").Trim(), "", "and no box shows a selection");
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
            view.Show(Paragraphs(60), 14);
            view.UpdateLayout();
            view.ScrollTo(view.ScrollableHeight * 0.3);
            view.UpdateLayout();
            var before = view.Place();
            view.Show(Paragraphs(60), 14);
            view.UpdateLayout();
            view.GoTo(before);
            var after = view.Place();
            Check.Eq(after.Block, before.Block, "the same block at the top after a redraw");
            Check.True(System.Math.Abs(after.Into - before.Into) < 1.5, $"as far into it: {before.Into:0.0} → {after.Into:0.0} px");
        }
        finally
        {
            w.Close();
        }
    }

    /// `n` paragraphs of mixed length (a long one every few: the heights not laid out are guessed wrong).
    private static List<Block> Paragraphs(int n)
    {
        var list = new List<Block>();
        for (int i = 0; i < n; i++) list.Add(P(string.Join(" ", System.Linq.Enumerable.Repeat($"문단{i} 글자", 1 + (i * 7 % 13) * 6)), 0, 8));
        return list;
    }

    /// The view in a window off the screen (laid out as on it).
    private static Window Hosted(MdView view)
    {
        var w = new Window { Content = view, Width = 480, Height = 320, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        w.Show();
        return w;
    }

    private static Paragraph P(string text, double top, double bottom) => new(new Run(text)) { Margin = new Thickness(0, top, 0, bottom) };

    /// The offset in its block's own document of the place `chars` letters into the paragraph.
    private static int Offset(Paragraph p, int chars)
    {
        var doc = (FlowDocument)p.Parent;
        return doc.ContentStart.GetOffsetToPosition(p.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetPositionAtOffset(chars));
    }
}
