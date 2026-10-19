using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bloom.Core;

namespace Bloom.Ui;

/// What a pane's Markdown (메모, 문서) has beyond a chat answer's: pictures, and checklist boxes that take clicks.
internal sealed class MarkdownExtras
{
    /// A checklist box was clicked, with its item's 0-based line in the source given to Render. True: the source took it
    /// and the box flips; false: it didn't (the text changed meanwhile), the box stays. Null: the boxes take no clicks.
    public Func<int, bool>? Toggle { get; init; }

    /// Where pictures' relative paths start (a document's folder); null: only absolute paths.
    public string? BaseDir { get; init; }

    /// A .md file in its document window (the document look); false: a note.
    public bool Document { get; init; }
}

/// A small Markdown renderer for chat answers and the Markdown panes (MdPane): headings, paragraphs, lists, checklists,
/// quotes, rules, pipe tables, fenced code (with a copy button), **bold**, *italic*, `code`, ~~strike~~, links and, in a
/// pane, pictures on this PC — one document per answer, so text can be dragged over and copied anywhere in it
/// (Controls\ReadText.cs). Korean words are kept together when lines wrap (display only; copying leaves the joiners out).
internal static class Markdown
{
    private static readonly Regex ListItem = MarkdownText.ListItem, TaskBox = MarkdownText.TaskBox; // one rule, drawn and ticked
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex RuleLine = new(@"^\s*([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex TableSep = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");
    private const int PictureWidth = 1600; // pictures wider than this are decoded at this width (memory)

    /// One rendering's settings, handed down the blocks.
    private sealed class Ctx(Action<string> openLink, double size, MarkdownExtras? extras)
    {
        public readonly Action<string> OpenLink = openLink;
        public readonly double Size = size;
        public readonly MarkdownExtras? Extras = extras;

        /// A .md file's document window: its own look (headings, lines joined as a Markdown file, more air).
        public bool Document => Extras?.Document == true;

        /// A chat answer's measure (at 14) at this size: code 12.5, a list marker's column 16 …
        public double Scaled(double at14) => at14 * Size / 14;
    }

    /// The whole answer as one selectable document: drag across paragraphs, lists and tables to copy.
    /// `extras`: a pane's pictures and clickable checklist boxes; null (chat, 새 소식): boxes are drawn only.
    public static ReadText Render(string md, Action<string> openLink, double size = 14, MarkdownExtras? extras = null)
    {
        var ctx = new Ctx(openLink, size, extras);
        var doc = ReadText.Doc(size);
        Blocks(md.Replace("\r\n", "\n"), 0, ctx, doc.Blocks);
        if (doc.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return new ReadText(doc);
    }

    /// A pane's text as its top-level blocks (a paragraph, a list item, a table, a quote …), each to be laid out on its
    /// own: MdPane lays out only the ones on screen, their margins collapsing as in one document.
    public static List<Block> BlocksOf(string md, Action<string> openLink, double size, MarkdownExtras extras)
    {
        var doc = ReadText.Doc(size);
        Blocks(md.Replace("\r\n", "\n"), 0, new Ctx(openLink, size, extras), doc.Blocks);
        var blocks = new List<Block>(doc.Blocks);
        doc.Blocks.Clear();
        return blocks;
    }

    /// `md`'s blocks into `into`; `lineBase`: the source line md's first line is (a quote's text keeps its lines' numbers).
    private static void Blocks(string md, int lineBase, Ctx ctx, BlockCollection into)
    {
        string[] lines = md.Split('\n');
        double size = ctx.Size;
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            string trim = line.TrimStart();
            if (MarkdownText.Fence(line) is { } fence)
            {
                string lang = trim[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && MarkdownText.Fence(lines[i]) != fence)
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                i++; // closing fence (or the end while streaming)
                into.Add(new BlockUIContainer(CodeBlock(code.ToString(), lang, ctx)) { Margin = Around(ctx) });
                continue;
            }
            if (trim.Length == 0)
            {
                i++;
                continue;
            }
            var h = Heading.Match(line);
            if (h.Success)
            {
                int level = h.Groups[1].Value.Length;
                Paragraph p;
                if (ctx.Document) p = DocHeading(level, into.Count == 0, ctx);
                else
                {
                    p = ReadText.Paragraph(size + (level <= 1 ? 3 : level == 2 ? 1.5 : 0.5), Theme.Semi);
                    p.Margin = new Thickness(0, into.Count == 0 ? 0 : 6, 0, 6);
                }
                Inline(h.Groups[2].Value, p.Inlines, ctx);
                into.Add(p);
                i++;
                continue;
            }
            if (RuleLine.IsMatch(line))
            {
                var ruleMargin = ctx.Document ? new Thickness(0, ctx.Scaled(16), 0, ctx.Scaled(16)) : new Thickness(0, 6, 0, 10);
                into.Add(new BlockUIContainer(new SmoothBorder { Height = 1, Background = Theme.P.Line }) { Margin = ruleMargin });
                i++;
                continue;
            }
            if (trim.StartsWith('|') && i + 1 < lines.Length && TableSep.IsMatch(lines[i + 1]))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    if (!TableSep.IsMatch(lines[i])) rows.Add(lines[i]);
                    i++;
                }
                into.Add(Table(rows, ctx));
                continue;
            }
            if (trim.StartsWith('>'))
            {
                int first = i;
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    string q = lines[i].TrimStart()[1..];
                    if (q.StartsWith(' ')) q = q[1..];
                    quote.Append(q).Append('\n');
                    i++;
                }
                var section = new Section
                {
                    BorderBrush = Theme.P.Line,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Margin = new Thickness(0, 0, 0, After(ctx)),
                };
                if (ctx.Document) section.Foreground = Theme.P.TextDim; // a quote reads as aside
                Blocks(quote.ToString().TrimEnd(), lineBase + first, ctx, section.Blocks); // one quote line per source line
                if (section.Blocks.LastBlock is { } lastQ) lastQ.Margin = new Thickness(lastQ.Margin.Left, lastQ.Margin.Top, lastQ.Margin.Right, 0);
                into.Add(section);
                continue;
            }
            if (ListItem.IsMatch(line))
            {
                int number = 0;
                Paragraph? prev = null;
                var itemLines = new List<string>(); // the item's text, its continuation lines after it
                while (i < lines.Length)
                {
                    var m = ListItem.Match(lines[i]);
                    if (!m.Success)
                    {
                        // a wrapped continuation line belongs to the previous item; a fence (indented too) opens code, as
                        // everywhere (MarkdownText.Fence: what ticking a box goes by)
                        if (lines[i].StartsWith("  ", StringComparison.Ordinal) && lines[i].Trim().Length > 0 && prev is not null && MarkdownText.Fence(lines[i]) is null)
                        {
                            itemLines.Add(ctx.Document ? lines[i].TrimStart() : lines[i].Trim());
                            i++;
                            continue;
                        }
                        break;
                    }
                    if (prev is not null) Lines(itemLines, prev.Inlines, ctx);
                    itemLines.Clear();
                    int indent = m.Groups[1].Value.Replace("\t", "    ").Length / 2;
                    string marker = m.Groups[2].Value;
                    string body = m.Groups[3].Value;
                    bool ordered = char.IsDigit(marker[0]);
                    number = ordered ? int.TryParse(marker.TrimEnd('.', ')'), out int n) ? n : number + 1 : number;
                    double left = Math.Min(indent, 4) * ctx.Scaled(18);
                    // a checklist item: its box where the marker would be
                    if (TaskBox.Match(body) is { Success: true } task)
                    {
                        prev = TaskItem(task.Groups[1].Value != " ", lineBase + i, left, ctx);
                        body = body[task.Length..];
                    }
                    else prev = Item(Theme.T(ordered ? number + "." : indent > 0 ? "◦" : "•", size, Theme.P.TextFaint, Theme.Regular, false), ctx.Scaled(ordered ? 22 : 16), left, ctx);
                    itemLines.Add(body);
                    into.Add(prev);
                    i++;
                }
                if (prev is not null)
                {
                    Lines(itemLines, prev.Inlines, ctx);
                    // the item's 3 + the list's own 8 after it (a document's: 5 + 9)
                    prev.Margin = new Thickness(prev.Margin.Left, 0, 0, ctx.Document ? ctx.Scaled(14) : 11);
                }
                continue;
            }
            // paragraph: until a blank line or another block
            var para = ReadText.Paragraph(size, Theme.Regular);
            para.Margin = new Thickness(0, 0, 0, After(ctx));
            var paraLines = new List<string>();
            while (i < lines.Length)
            {
                string l = lines[i];
                string lt = l.TrimStart();
                if (lt.Length == 0 || MarkdownText.Fence(l) is not null || Heading.IsMatch(l) || ListItem.IsMatch(l) || lt.StartsWith('>') || (lt.StartsWith('|') && i + 1 < lines.Length && TableSep.IsMatch(lines[i + 1])) || RuleLine.IsMatch(l)) break;
                paraLines.Add(ctx.Document ? lt : l.TrimEnd());
                i++;
            }
            Lines(paraLines, para.Inlines, ctx);
            into.Add(para);
        }
    }

    /// The room under a paragraph or a quote: 8, a document's 12.
    private static double After(Ctx ctx) => ctx.Document ? ctx.Scaled(12) : 8;

    /// Around a code block or a table: 2 above and 10 under, a document's 4 and 14.
    private static Thickness Around(Ctx ctx) => ctx.Document ? new Thickness(0, ctx.Scaled(4), 0, ctx.Scaled(14)) : new Thickness(0, 2, 0, 10);

    /// A paragraph's (or a list item's) source lines as its text. A document joins them as a Markdown file does
    /// (CommonMark): a line ending in two spaces or a backslash breaks there, any other joins the next with a space
    /// (Inline's Text, over the whole: bold or code may go on over a line end). A note or a chat answer breaks at each.
    private static void Lines(List<string> lines, InlineCollection into, Ctx ctx)
    {
        if (ctx.Document)
        {
            Inline(string.Join('\n', lines).TrimEnd(), into, ctx);
            return;
        }
        for (int k = 0; k < lines.Count; k++)
        {
            if (k > 0) into.Add(new LineBreak());
            Inline(lines[k], into, ctx);
        }
    }

    /// A document's heading: at text size 14 # 24, ## 19, ### 16, the rest 14.5; more room above (28, 20, 16; none
    /// first) than under (8), a faint line under #.
    private static Paragraph DocHeading(int level, bool first, Ctx ctx)
    {
        double size = ctx.Scaled(level switch { 1 => 24, 2 => 19, 3 => 16, _ => 14.5 });
        var p = ReadText.Paragraph(size, Theme.Semi);
        p.LineHeight = Math.Round(size * 1.3);
        double above = first ? 0 : ctx.Scaled(level <= 2 ? 28 : level == 3 ? 20 : 16);
        p.Margin = new Thickness(0, above, 0, ctx.Scaled(level == 1 ? 12 : 8));
        if (level == 1)
        {
            p.Padding = new Thickness(0, 0, 0, ctx.Scaled(8));
            p.BorderBrush = Theme.P.Line;
            p.BorderThickness = new Thickness(0, 0, 0, 1);
        }
        return p;
    }

    /// One list item: the marker (a line of text's height) in a column of its own (`width`), the text hanging beside it
    /// when it wraps.
    private static Paragraph Item(FrameworkElement mark, double width, double indent, Ctx ctx)
    {
        double size = ctx.Size;
        var p = ReadText.Paragraph(size, Theme.Regular);
        p.Margin = new Thickness(indent + width, 0, 0, ctx.Document ? ctx.Scaled(5) : 3);
        p.TextIndent = -width;
        mark.Width = width;
        // an inline element sits on the baseline by its bottom edge: the part under the marker's own baseline hangs below
        mark.Margin = new Thickness(0, 0, 0, -(Theme.Sans.LineSpacing - Theme.Sans.Baseline) * size);
        p.Inlines.Add(new InlineUIContainer(mark) { BaselineAlignment = BaselineAlignment.Baseline });
        return p;
    }

    /// A checklist item: a box in the marker's column, ticked for [x] (its text dimmed). With extras' Toggle a click
    /// hands `line` over and flips the box once the source took it; otherwise (chat) it is only drawn.
    private static Paragraph TaskItem(bool done, int line, double indent, Ctx ctx)
    {
        double size = ctx.Size, s = Math.Round(size * 0.95);
        var box = new SmoothBorder
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(Math.Round(s * 0.3)),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // over the box rather than in it: the glyph's line is taller than the box and would be cut off there
        var tick = Theme.Glyph(Search.G.Check, Math.Round(size * 0.75), Theme.P.AccentInk);
        tick.Width = s;
        tick.LineHeight = double.NaN; // not the paragraph's, which it would take through the document
        tick.TextAlignment = TextAlignment.Center;
        tick.HorizontalAlignment = HorizontalAlignment.Left;
        // the whole spot in the column takes the click, a line of text high
        var spot = new Grid { Height = Theme.Sans.LineSpacing * size, Background = Brushes.Transparent, Children = { box, tick } };
        var p = Item(spot, s + Math.Round(size * 0.55), indent, ctx);
        bool hover = false;
        void Show()
        {
            box.Background = done ? Theme.P.Accent : Brushes.Transparent;
            box.BorderBrush = done || hover ? Theme.P.Accent : Theme.P.TextFaint;
            tick.Visibility = done ? Visibility.Visible : Visibility.Hidden;
            if (done) p.Foreground = Theme.P.TextDim;
            else p.ClearValue(TextElement.ForegroundProperty);
        }
        Show();
        if (ctx.Extras?.Toggle is { } toggle)
        {
            spot.Cursor = Cursors.Hand;
            spot.MouseEnter += (_, _) =>
            {
                hover = true;
                Show();
            };
            spot.MouseLeave += (_, _) =>
            {
                hover = false;
                Show();
            };
            spot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!toggle(line)) return;
                done = !done;
                Show();
            };
        }
        return p;
    }

    public static TextBlock Para(double size, FontWeight weight) => new()
    {
        FontFamily = Theme.Sans,
        FontSize = size,
        FontWeight = weight,
        Foreground = Theme.P.Text,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Math.Round(size * 1.6),
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// Fenced code: monospace on its own tint, with 복사 for all of it. It is drawn as a plain TextBlock and becomes
    /// selectable (a ReadText, laid out the same) when the mouse first comes over it: a text box of its own inside the
    /// answer's costs about 20 KB of memory, which most code blocks are never dragged over to need.
    private static SmoothBorder CodeBlock(string code, string lang, Ctx ctx)
    {
        double size = ctx.Scaled(12.5), lineHeight = Math.Round(ctx.Scaled(19));
        var grid = new Grid();
        var holder = new Border
        {
            Margin = new Thickness(0, lang.Length > 0 ? 18 : 0, 0, 0),
            Background = Brushes.Transparent, // the mouse finds it between the lines too
            Child = new TextBlock { Text = code, FontFamily = Theme.Mono, FontSize = size, Foreground = Theme.P.Text, TextWrapping = TextWrapping.Wrap, LineHeight = lineHeight },
        };
        holder.MouseEnter += Selectable;
        void Selectable(object sender, MouseEventArgs e)
        {
            holder.MouseEnter -= Selectable;
            var p = new Paragraph(new Run(code)) { FontFamily = Theme.Mono, FontSize = size, LineHeight = lineHeight, Margin = new Thickness(0) };
            holder.Child = new ReadText(ReadText.Doc(size, p));
        }
        grid.Children.Add(holder);
        if (lang.Length > 0)
        {
            var l = Theme.T(lang, 11, Theme.P.TextFaint, Theme.Medium);
            l.VerticalAlignment = VerticalAlignment.Top;
            l.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(l);
        }
        var copyText = Theme.T(Tr("복사", "Copy"), 11, Theme.P.TextDim, Theme.Medium, false);
        var copy = new SmoothBorder
        {
            Child = copyText,
            Padding = new Thickness(7, 1, 7, 2),
            CornerRadius = new CornerRadius(5),
            Background = Theme.P.Key,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, -2, -4, 0),
        };
        copy.MouseLeftButtonUp += (_, e) =>
        {
            Search.Launch.Copy(code);
            copyText.Text = Tr("복사 완료", "Copied");
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                copyText.Text = Tr("복사", "Copy");
            };
            t.Start();
            e.Handled = true;
        };
        grid.Children.Add(copy);
        return new SmoothBorder
        {
            Background = Theme.P.Code,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 10),
            Child = grid,
        };
    }

    private static Table Table(List<string> rows, Ctx ctx)
    {
        double size = ctx.Size;
        var cells = new List<string[]>();
        int cols = 0;
        foreach (string r in rows)
        {
            string t = r.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|')) t = t[..^1];
            var parts = t.Split('|');
            for (int k = 0; k < parts.Length; k++) parts[k] = parts[k].Trim();
            cols = Math.Max(cols, parts.Length);
            cells.Add(parts);
        }
        var table = new Table { CellSpacing = 0, Margin = Around(ctx) };
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var widths = new double[cols];
        for (int r = 0; r < cells.Count; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                var p = ReadText.Paragraph(size - 1, r == 0 ? Theme.Semi : Theme.Regular);
                p.Margin = new Thickness(0);
                p.LineHeight = Math.Round((size - 1) * 1.5);
                if (c < cells[r].Length) Inline(cells[r][c], p.Inlines, ctx);
                widths[c] += Width(p.Inlines, p.FontSize, p.FontWeight);
                var cell = new TableCell(p) { Padding = new Thickness(8, 6, 8, 6), BorderBrush = Theme.P.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
                if (r == 0) cell.Background = Theme.P.Tile; // the header row on a faint tint
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        foreach (double share in ColumnShares(widths)) table.Columns.Add(new TableColumn { Width = new GridLength(share, GridUnitType.Star) });
        return table;
    }

    /// Inlines' text as wide as it is drawn on one line, each run in its own font (code's mono, the rest the text's).
    private static double Width(InlineCollection inlines, double size, FontWeight weight)
    {
        double w = 0;
        foreach (var inline in inlines)
        {
            if (inline is Span span) w += Width(span.Inlines, size, weight);
            else if (inline is Run run && run.Text.Length > 0)
            {
                var family = run.ReadLocalValue(TextElement.FontFamilyProperty) as FontFamily ?? Theme.Sans;
                double at = run.ReadLocalValue(TextElement.FontSizeProperty) is double s ? s : size;
                w += new FormattedText(run.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), at, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
            }
        }
        return w;
    }

    /// Each column's share of a table's width by how wide its cells' text is (all rows, on one line), kept between 14
    /// and 45 % (fewer columns: what they must): a column of long text or code gets the room, a short one still holds a
    /// word or a path ("Installer/", "WeatherService." in a 760 px document window). The shares are one scale times the
    /// widths, clamped, the scale found so they make the whole width.
    internal static double[] ColumnShares(double[] widths)
    {
        int cols = widths.Length;
        // two columns go by their text too (45 % each couldn't fill the width); eight or more keep half an even share
        double lo = cols * 0.14 < 1 ? 0.14 : 0.5 / cols, hi = cols == 2 ? 1 - lo : Math.Max(0.45, 1.0 / cols);
        double low = 0, high = 1;
        foreach (double w in widths) high = Math.Max(high, hi / Math.Max(w, 1)); // every column at hi: at least the whole
        for (int round = 0; round < 60; round++)
        {
            double scale = (low + high) / 2, sum = 0;
            foreach (double w in widths) sum += Math.Clamp(scale * w, lo, hi);
            if (sum > 1) high = scale;
            else low = scale;
        }
        var share = new double[cols];
        double total = 0;
        for (int c = 0; c < cols; c++) total += share[c] = Math.Clamp(low * widths[c], lo, hi);
        for (int c = 0; c < cols; c++) share[c] /= total;
        return share;
    }

    // ── inline ──────────────────────────────────────────────────────────────────────────────────
    private const string TokenPattern =
        @"(\*\*(?<b>.+?)\*\*)|(__(?<b2>.+?)__)|(~~(?<s>.+?)~~)|(`(?<c>[^`]+)`)|(\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\))|(?<u>https?://[^\s<>()\]]+[^\s<>()\].,;:!?'""])|((?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*]))|((?<![\w_])_(?<i2>[^_\s][^_]*?)_(?![\w_]))";
    // Singleline: a document's paragraph comes whole, its line ends in it (Lines), and **bold** may go over one
    private static readonly Regex Token = new(TokenPattern, RegexOptions.Compiled | RegexOptions.Singleline);
    // a pane's: ![alt](src) pictures too (a chat answer keeps reading them as "!" and a link)
    private static readonly Regex PaneToken = new(@"(!\[(?<pa>[^\]]*)\]\((?<ps>[^)\s]+)\))|" + TokenPattern, RegexOptions.Compiled | RegexOptions.Singleline);

    private static void Inline(string s, InlineCollection into, Ctx ctx)
    {
        int at = 0;
        foreach (Match m in (ctx.Extras is null ? Token : PaneToken).Matches(s))
        {
            if (m.Index > at) Text(s[at..m.Index], into);
            if (m.Groups["pa"].Success)
            {
                into.Add(Picture(m.Groups["pa"].Value, m.Groups["ps"].Value, ctx));
            }
            else if (m.Groups["b"].Success || m.Groups["b2"].Success)
            {
                var b = new Bold();
                Inline(m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["b2"].Value, b.Inlines, ctx);
                into.Add(b);
            }
            else if (m.Groups["s"].Success)
            {
                var sp = new Span { TextDecorations = TextDecorations.Strikethrough };
                Inline(m.Groups["s"].Value, sp.Inlines, ctx);
                into.Add(sp);
            }
            else if (m.Groups["c"].Success)
            {
                into.Add(new Run(CodeText(m.Groups["c"].Value)) { FontFamily = Theme.Mono, FontSize = ctx.Scaled(12.5), Background = Theme.P.Code });
            }
            else if (m.Groups["lt"].Success)
            {
                into.Add(Link(m.Groups["lt"].Value, m.Groups["lu"].Value, ctx.OpenLink));
            }
            else if (m.Groups["u"].Success)
            {
                string u = m.Groups["u"].Value;
                into.Add(Link(u.Length > 60 ? u[..57] + "…" : u, u, ctx.OpenLink));
            }
            else if (m.Groups["i"].Success || m.Groups["i2"].Success)
            {
                var it = new Italic();
                Inline(m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value, it.Inlines, ctx);
                into.Add(it);
            }
            at = m.Index + m.Length;
        }
        if (at < s.Length) Text(s[at..], into);
    }

    /// Plain text. Only a document's has line ends in it (Lines): one after two spaces or a backslash breaks the line,
    /// any other joins the lines with a space.
    private static void Text(string s, InlineCollection into)
    {
        string[] parts = s.Split('\n');
        for (int k = 0; k < parts.Length; k++)
        {
            string part = parts[k];
            bool hard = false;
            if (k < parts.Length - 1)
            {
                if (part.EndsWith("  ", StringComparison.Ordinal)) hard = true;
                else if (part.EndsWith('\\'))
                {
                    hard = true;
                    part = part[..^1];
                }
                part = part.TrimEnd(' ', '\t');
                if (!hard) part += " ";
            }
            if (part.Length > 0) into.Add(new Run(Hangul.KeepAll(part)));
            if (hard) into.Add(new LineBreak());
        }
    }

    /// Inline code as drawn: a space of padding each side that no line break parts from it, and in a long one a chance
    /// to break after . / \ _ - before a letter or digit (Hangul.CodePad, BreakHint; copied as written: Hangul.Unjoin).
    /// A line end in it (a document's code over two lines) is a space.
    private static string CodeText(string code)
    {
        var sb = new StringBuilder(code.Length + 8).Append(Hangul.CodePad);
        for (int k = 0; k < code.Length; k++)
        {
            char c = code[k] == '\n' ? ' ' : code[k];
            sb.Append(c);
            if ((c is '.' or '/' or '\\' or '_' or '-') && k + 1 < code.Length && char.IsLetterOrDigit(code[k + 1])) sb.Append(Hangul.BreakHint);
        }
        return sb.Append(Hangul.CodePad).ToString();
    }

    /// ![alt](src): a picture on this PC (absolute, or from extras' BaseDir), fitted to the width; one on the web is a
    /// link with its alt text (nothing is fetched just by showing the text); one that can't be read is its alt text.
    private static System.Windows.Documents.Inline Picture(string alt, string src, Ctx ctx)
    {
        string shown = alt.Length > 0 ? alt : src;
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Link(shown, src, ctx.OpenLink);
        if (Load(src, ctx.Extras?.BaseDir) is not { } bitmap)
        {
            var words = new Span { Foreground = Theme.P.TextFaint };
            Text(shown, words.Inlines);
            return words;
        }
        // the line's width is what an inline element is measured against: a wide one shrinks to it
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, ToolTip = alt.Length > 0 ? alt : null };
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
    }

    /// The picture file read whole (OnLoad: the file isn't held open), at most PictureWidth pixels wide; null when it
    /// is no file here, is on another computer (DocLink.Picture: refused before anything opens it) or can't be read.
    private static BitmapSource? Load(string src, string? baseDir)
    {
        try
        {
            if (DocLink.Picture(src, baseDir) is not { } path || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            int width = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (width > PictureWidth) bitmap.DecodePixelWidth = PictureWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private static Hyperlink Link(string text, string url, Action<string> openLink)
    {
        var h = new Hyperlink { Foreground = Theme.P.AccentText, TextDecorations = null, Cursor = Cursors.Hand };
        Text(text, h.Inlines); // a document's link text over a line end joins as any text does
        h.Click += (_, _) => openLink(url);
        h.MouseEnter += (_, _) => h.TextDecorations = TextDecorations.Underline;
        h.MouseLeave += (_, _) => h.TextDecorations = null;
        return h;
    }
}
