using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bloom.Core;
using Bloom.Ui;

namespace Bloom.Tests;

/// Checklists and pictures as Ui\Markdown.cs draws them, and the pane that ticks a note's boxes (Ui\MdPane.cs).
internal static class MarkdownTests
{
    private const string Tasks = "# 장보기\n- [ ] 우유\n- [x] 계란\n\n> 1. [ ] 인용 안\n> - [X] 둘째\n\n```\n- [ ] 코드 안\n```\n* [ ] 별표\n+ [ ] 더하기";

    public static void ChecklistLines()
    {
        var got = new List<int>();
        var shown = Markdown.Render(Tasks, _ => { }, 14, new MarkdownExtras { Toggle = line => { got.Add(line); return true; } });
        var boxes = Boxes(shown.Document.Blocks).ToList();
        Check.Eq(boxes.Count, 6, "a box per checklist item, none in code");
        foreach (var b in boxes) Click(b);
        Check.Eq(string.Join(",", got), "1,2,4,5,10,11", "each box hands over its own source line, in a quote too");

        var chat = Markdown.Render(Tasks, _ => { }, 14);
        var chatBoxes = Boxes(chat.Document.Blocks).ToList();
        Check.Eq(chatBoxes.Count, 6, "chat draws the boxes too");
        Check.True(chatBoxes.All(b => b.Cursor is null), "but they take no clicks there");

        // code fenced under a list item is code, as MarkdownText.ToggleTask reads it: every box drawn takes its click
        string inList = "- a\n  ```\n  - [ ] in code\n  ```\n- [ ] after\n- [x]done";
        string text = inList;
        var drawn = Markdown.Render(inList, _ => { }, 14, new MarkdownExtras
        {
            Toggle = line =>
            {
                if (MarkdownText.Line(text, line) is not { } l || MarkdownText.ToggleTask(text, line, l) is not { } next) return false;
                text = next;
                return true;
            },
        });
        var listBoxes = Boxes(drawn.Document.Blocks).ToList();
        Check.Eq(listBoxes.Count, 1, "no box in the code, none for \"[x]done\"");
        Click(listBoxes[0]);
        Check.Eq(text, "- a\n  ```\n  - [ ] in code\n  ```\n- [x] after\n- [x]done", "the one drawn ticks");
    }

    public static void ChatLooksTheSame()
    {
        var chat = Markdown.Render("```\ncode\n```\n\n`inline` ![alt](https://example.com/a.png)", _ => { });
        var code = (TextBlock)((Border)((Grid)((SmoothBorder)((BlockUIContainer)chat.Document.Blocks.FirstBlock).Child).Child).Children[0]).Child;
        Check.Eq(code.FontSize, 12.5, "code at 12.5 in a chat answer");
        var para = (Paragraph)chat.Document.Blocks.LastBlock;
        Check.Eq(para.Inlines.OfType<Run>().First(r => r.Text.Contains("inline", System.StringComparison.Ordinal)).FontSize, 12.5, "inline code too");
        Check.True(para.Inlines.OfType<Hyperlink>().Any() && !para.Inlines.OfType<InlineUIContainer>().Any(), "a picture in chat stays \"!\" and a link");
        var big = Markdown.Render("```\ncode\n```", _ => { }, 28, new MarkdownExtras());
        var bigCode = (TextBlock)((Border)((Grid)((SmoothBorder)((BlockUIContainer)big.Document.Blocks.FirstBlock).Child).Child).Children[0]).Child;
        Check.Eq(bigCode.FontSize, 25.0, "code follows the size");
    }

    public static void Pictures()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bloom-md-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "wide.png");
            var pixels = new byte[3000 * 4 * 4];
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(3000, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 3000 * 4)));
            using (var s = File.Create(file)) encoder.Save(s);

            var shown = Markdown.Render("![넓은 그림](wide.png)\n\n![웹](https://example.com/a.png)\n\n![없는 그림](missing.png)\n\n- ![절대 경로](" + file + ")", _ => { }, 14, new MarkdownExtras { BaseDir = dir });
            var paras = shown.Document.Blocks.OfType<Paragraph>().ToList();
            var image = (Image)((InlineUIContainer)paras[0].Inlines.FirstInline).Child;
            Check.Eq(((BitmapSource)image.Source).PixelWidth, 1600, "a wide picture is read at most 1600 pixels wide");
            Check.True(paras[3].Inlines.OfType<InlineUIContainer>().Any(c => c.Child is Image), "an absolute path, in a list item");
            // fitted to the text's width, the list item's indent too
            shown.Measure(new Size(300, double.PositiveInfinity));
            shown.Arrange(new Rect(0, 0, 300, shown.DesiredSize.Height));
            shown.UpdateLayout();
            Check.True(image.ActualWidth is > 0 and <= 300, $"a wide picture fits the width (got {image.ActualWidth:0.#})");
            var inList = paras[3].Inlines.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Image>().First();
            Check.True(inList.ActualWidth is > 0 and <= 300 - 16, $"and the room beside a list marker (got {inList.ActualWidth:0.#})");
            File.Delete(file);
            Check.True(!File.Exists(file), "and the file isn't held open");
            Check.True(paras[1].Inlines.FirstInline is Hyperlink, "a picture on the web is a link");
            Check.Eq(AltText(paras[2].Inlines.FirstInline), "없는 그림", "a missing one is its alt text");
            Check.Eq(AltText(Markdown.Render("![x](wide.png)", _ => { }, 14, new MarkdownExtras()).Document.Blocks.OfType<Paragraph>().First().Inlines.FirstInline), "x", "a relative path with no folder: its alt text");
            // a network picture is its alt text, refused before anything looks for it (a .invalid host: nothing to reach)
            var net = Markdown.Render(@"![a](\\bloom-test.invalid\share\a.png) ![b](file://bloom-test.invalid/s/b.png) ![c](%5C%5Cbloom-test.invalid%5Cs%5Cc.png)", _ => { }, 14, new MarkdownExtras { BaseDir = dir });
            var netInlines = net.Document.Blocks.OfType<Paragraph>().First().Inlines.ToList();
            Check.True(!netInlines.OfType<InlineUIContainer>().Any() && netInlines.Count(i => AltText(i).Length > 0) == 3, "network pictures: alt text");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public static void PaneTicksTheText()
    {
        int was = Settings.Current.MdTextSize;
        try
        {
            var pane = new MdPane("# 할 일\r\n- [ ] 우유\r\n- [x] 계란\r\n", true, null, false, false);
            string? edited = null;
            pane.Edited += t => edited = t;
            var view = (MdView)pane.KeyTarget;
            object shown = view.ItemsSource;
            var boxes = Boxes(view.Blocks).ToList();
            Click(boxes[0]);
            Check.Eq(edited, "# 할 일\r\n- [x] 우유\r\n- [x] 계란\r\n", "a click ticks the box in the text, CRLF kept, and hands it over");
            Check.Eq(pane.Text, edited, "the pane holds the new text");
            Check.True(ReferenceEquals(view.ItemsSource, shown), "without drawing the view again");
            Click(boxes[0]);
            Check.Eq(edited, "# 할 일\r\n- [ ] 우유\r\n- [x] 계란\r\n", "a second click clears it again");

            pane.Text = "# 할 일\r\n- [x] 두유\r\n";
            Check.True(!ReferenceEquals(view.ItemsSource, shown), "a text from outside is drawn again");
            Check.Eq(edited, "# 할 일\r\n- [ ] 우유\r\n- [x] 계란\r\n", "and isn't handed back as an edit");

            var readOnly = new MdPane("- [ ] a", true, null, true, false);
            Check.True(Boxes(((MdView)readOnly.KeyTarget).Blocks).All(b => b.Cursor is null), "a read-only pane's boxes take no clicks");

            pane.SetTextSize(40);
            Check.Eq(Settings.Current.MdTextSize, Settings.MdTextMax, "the size stops at its largest");
            pane.SetTextSize(3);
            Check.Eq(MdPane.TextSize, Settings.MdTextMin, "and its smallest");
            pane.InView = false;
            Check.True(pane.KeyTarget is TextBox, "편집: the editor");
        }
        finally
        {
            Settings.Current.MdTextSize = was;
        }
    }

    /// A picture shown as its alt text (faint text, not a link): that text; anything else: "".
    private static string AltText(Inline? inline) => inline is Span and not Hyperlink ? new TextRange(inline.ContentStart, inline.ContentEnd).Text.Replace("\u2060", "", System.StringComparison.Ordinal).Trim() : "";

    /// The checklist boxes' click spots, in document order.
    private static IEnumerable<FrameworkElement> Boxes(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Section s)
                foreach (var b in Boxes(s.Blocks)) yield return b;
            else if (block is Paragraph p && p.Inlines.FirstInline is InlineUIContainer { Child: Grid { Children: [SmoothBorder, ..] } spot })
                yield return spot;
        }
    }

    private static void Click(FrameworkElement spot) =>
        spot.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
}
