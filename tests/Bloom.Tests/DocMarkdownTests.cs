using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Bloom.Core;
using Bloom.Ui;

namespace Bloom.Tests;

/// A .md file's look (Ui\Markdown.cs, MarkdownExtras.Document): lines joined as a Markdown file, hard breaks, table
/// columns by their text, inline code that wraps whole and copies as written.
internal static class DocMarkdownTests
{
    private static readonly MarkdownExtras Document = new() { Document = true };

    public static void SoftWrapJoinsInDocuments()
    {
        const string md = "Read this first before\nchanging anything.\n\n- an item that\n  goes on here\n- next";
        var doc = Markdown.Render(md, _ => { }, 14, Document).Document.Blocks.OfType<Paragraph>().ToList();
        Check.Eq(Plain(doc[0]), "Read this first before changing anything.", "a document joins a paragraph's lines with a space");
        Check.Eq(Plain(doc[1]), "an item that goes on here", "and a list item's continuation lines");
        foreach (var (extras, what) in new[] { (new MarkdownExtras(), "a note"), ((MarkdownExtras?)null, "a chat answer") })
        {
            var other = Markdown.Render(md, _ => { }, 14, extras).Document.Blocks.OfType<Paragraph>().ToList();
            Check.Eq(Plain(other[0]), "Read this first before\nchanging anything.", what + " keeps each line");
            Check.Eq(Plain(other[1]), "an item that\ngoes on here", what + "'s list item too");
        }
        var bold = Markdown.Render("**bold over\ntwo lines** after", _ => { }, 14, Document).Document.Blocks.OfType<Paragraph>().First();
        Check.Eq(Plain(bold.Inlines.OfType<Bold>().Single()), "bold over two lines", "bold goes on over a line end");
    }

    public static void HardBreaks()
    {
        var p = Markdown.Render("two spaces  \nback\\\nslash\nsoft  \t\nlast\\", _ => { }, 14, Document).Document.Blocks.OfType<Paragraph>().Single();
        Check.Eq(Plain(p), "two spaces\nback\nslash soft last\\", "two spaces or a backslash break the line (not a tab); the last line's backslash stays");
        var code = Markdown.Render("see `Logs\\\nmore` here", _ => { }, 14, Document).Document.Blocks.OfType<Paragraph>().Single();
        Check.Eq(Plain(code), "see  Logs\\ more  here", "a code span over a line end: a space, no break");
    }

    public static void TableColumnsByText()
    {
        var even = Markdown.ColumnShares([40, 40]);
        Check.Near(even[0], 0.5, 1e-6, "equal text, equal columns");
        var wide = Markdown.ColumnShares([10, 10, 5000]);
        Check.Near(wide[2], 0.45, 1e-6, "a long column stops at 45 %");
        Check.Near(wide[0], 0.275, 1e-6, "the others share the rest");
        Check.Near(Markdown.ColumnShares([10, 5000])[1], 0.86, 1e-6, "two columns by their text too: the short one keeps 14 %");
        var eight = Markdown.ColumnShares([10, 1000, 1000, 1000, 1000, 1000, 1000, 1000]);
        Check.Near(eight[0], 0.0625, 1e-6, "eight columns: a short one keeps half an even share");
        Check.True(eight.Skip(1).All(s => System.Math.Abs(s - 0.9375 / 7) < 1e-6), "the long ones the rest");
        var many = Markdown.ColumnShares([10, 1000, 1000, 1000, 1000]);
        Check.Near(many[0], 0.14, 1e-6, "a short column keeps 14 %");
        Check.Near(many.Sum(), 1, 1e-6, "the shares make the whole width");
        Check.True(many.Skip(1).All(s => System.Math.Abs(s - 0.215) < 1e-6), "the long ones share the rest evenly");
        var some = Markdown.ColumnShares([100, 200, 300]);
        Check.Near(some[1] / some[0], 2, 1e-6, "within the bounds, by width");

        var table = Markdown.Render("| Assembly | Holds | # |\n|---|---|---|\n| `Contoso.WeatherService.Runtime` | scene parts, shaders | 1 |\n| `Contoso.WeatherService.Core` | logic without drawing | 2 |", _ => { }).Document.Blocks.OfType<Table>().Single();
        var stars = table.Columns.Select(c => c.Width).ToList();
        Check.True(stars.All(s => s.IsStar), "star widths");
        double sum = stars.Sum(s => s.Value);
        Check.True(stars[0].Value > stars[1].Value && stars[1].Value > stars[2].Value, $"the code column widest, the short one least (got {string.Join(", ", stars.Select(s => (s.Value / sum).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))})");
        Check.Near(stars[2].Value / sum, 0.14, 1e-6, "the short one at its 14 %");
        var header = table.RowGroups[0].Rows[0].Cells[0];
        Check.True(header.Background is not null && table.RowGroups[0].Rows[1].Cells[0].Background is null, "the header row on a tint");
    }

    public static void CodeCopiesAsWritten()
    {
        var answer = Markdown.Render("값은 `Contoso.WeatherService.Runtime`에 있어요 `a-b/c_d\\e`", _ => { });
        var code = ((Paragraph)answer.Document.Blocks.FirstBlock).Inlines.OfType<Run>().First(r => r.Text.Contains("Contoso", System.StringComparison.Ordinal)).Text;
        Check.Eq(code, $"{Hangul.CodePad}Contoso.{Hangul.BreakHint}WeatherService.{Hangul.BreakHint}Runtime{Hangul.CodePad}", "drawn: padding that holds on, break chances after the dots");
        answer.SelectAll();
        Check.Eq(answer.SelectedText, "값은  Contoso.WeatherService.Runtime 에 있어요  a-b/c_d\\e ", "copied as before: spaces round the code, nothing put in");
        Check.Eq(Hangul.Unjoin("가\u2060나 \u2007x.\u200By\u2007"), "가나  x.y ", "Unjoin: joiners and break chances out, padding a space");
        Check.Eq(Hangul.Unjoin("plain"), "plain", "plain text as it is");
    }

    public static void ChecklistLinesWithJoinedText()
    {
        const string md = "# T\nan intro that\nwraps here\n\n- [ ] one\n  continues\n  and more\n- [x] two\n\n> quoted\n> text\n> - [ ] three\n";
        string text = md;
        var shown = Markdown.Render(md, _ => { }, 14, new MarkdownExtras
        {
            Document = true,
            Toggle = line =>
            {
                if (MarkdownText.Line(text, line) is not { } l || MarkdownText.ToggleTask(text, line, l) is not { } next) return false;
                text = next;
                return true;
            },
        });
        var spots = Spots(shown.Document.Blocks).ToList();
        Check.Eq(spots.Count, 3, "a box per checklist item");
        foreach (var s in spots) s.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Check.Eq(text, "# T\nan intro that\nwraps here\n\n- [x] one\n  continues\n  and more\n- [ ] two\n\n> quoted\n> text\n> - [x] three\n", "each box ticks its own line");
    }

    /// A paragraph's (or span's) text as copied: line breaks as \n, the display characters out.
    private static string Plain(TextElement e)
    {
        var sb = new StringBuilder();
        void Walk(IEnumerable<Inline> inlines)
        {
            foreach (var i in inlines)
            {
                if (i is Run r) sb.Append(r.Text);
                else if (i is LineBreak) sb.Append('\n');
                else if (i is Span s) Walk(s.Inlines);
            }
        }
        Walk(e is Paragraph p ? p.Inlines : ((Span)e).Inlines);
        return Hangul.Unjoin(sb.ToString());
    }

    /// The checklist boxes' click spots, in document order.
    private static IEnumerable<FrameworkElement> Spots(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Section s)
                foreach (var b in Spots(s.Blocks)) yield return b;
            else if (block is Paragraph p && p.Inlines.FirstInline is InlineUIContainer { Child: Grid { Children: [SmoothBorder, ..] } spot })
                yield return spot;
        }
    }
}
