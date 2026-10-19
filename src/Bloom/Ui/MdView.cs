using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bloom.Ui;

/// MdPane's 보기: the Markdown's top-level blocks (Markdown.BlocksOf), each in a ReadText of its own, in a virtualizing
/// list — only the blocks on screen are laid out, so a long file opens and follows a new width at once (one ReadText
/// over a 250 KB file took 8–20 s for each width it was given). The space between two blocks is the larger of their
/// margins, as in one document, and the text keeps to a reading width (ReadingEms × the text size) in the middle of a
/// wide window. A drag selects over blocks as in one document, scrolling on past the top or bottom; Ctrl+C and the
/// menu's 복사 copy all of it, Ctrl+A selects everything. Inside one block the box selects by itself (a double-click's
/// word, Shift+arrows).
internal sealed class MdView : ItemsControl, ISelectionOwner
{
    private const double ReadingEms = 54;
    private const double Top = 12, Bottom = 14; // above the first block, below the last
    private static readonly Thickness Side = new(16, 0, 14, 0);

    /// A block in its own document for good (selection offsets and copying go by it), the space above it (the
    /// collapsed margin) and below it (only the last one's).
    private sealed record Item(int Index, Block Block, FlowDocument Doc, double Above, double Below);

    /// A place in the text: a block, and an offset in its document.
    private readonly record struct Spot(int Block, int At) : IComparable<Spot>
    {
        public int CompareTo(Spot other) => Block != other.Block ? Block.CompareTo(other.Block) : At.CompareTo(other.At);
    }

    private List<Item> items = [];
    private ScrollViewer? scroller;
    private ItemsPresenter? presenter;
    private double readingWidth = ReadingEms * 14;
    private Spot? anchor, end; // the selection's two ends as made (either order); null: nothing selected over blocks
    private int pressed = -1; // a press in a block, until the drag leaves it
    private Spot pressAt;
    private bool dragging; // the drag left its block (or began between blocks): this view selects, not the boxes
    private bool swapping; // Show putting the new blocks in: the old boxes' selections are no one's now
    private readonly DispatcherTimer edge = new() { Interval = TimeSpan.FromMilliseconds(30) };

    public MdView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer), "scroll");
        scroll.SetValue(TemplateProperty, Launcher.ThinScroll());
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(FocusableProperty, false);
        var shown = new FrameworkElementFactory(typeof(ItemsPresenter), "items");
        shown.SetValue(MarginProperty, Side);
        scroll.AppendChild(shown);
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroll };
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Page);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(1));
        ApplyTemplate();
        edge.Tick += (_, _) => Carry();
        // the keyboard back (the window active again): boxes that couldn't take theirs meanwhile draw the selection now
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is true && Span() is not null) PaintAll();
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        scroller = GetTemplateChild("scroll") as ScrollViewer;
        presenter = GetTemplateChild("items") as ItemsPresenter;
        if (presenter is not null) presenter.MaxWidth = readingWidth;
    }

    public double VerticalOffset => scroller?.VerticalOffset ?? 0;

    public double ScrollableHeight => scroller?.ScrollableHeight ?? 0;

    public void ScrollTo(double offset) => scroller?.ScrollToVerticalOffset(offset);

    public void ScrollToEnd() => scroller?.ScrollToEnd();

    /// Where the view is scrolled to: the first block on screen, and how many pixels of it (with the space above it)
    /// are above the top. A share of the height wouldn't do: the height of the blocks not laid out is only guessed.
    public (int Block, double Into) Place()
    {
        if (scroller is null) return (0, 0);
        foreach (var (host, it) in Shown())
        {
            double top = host.TranslatePoint(new Point(0, 0), scroller).Y - it.Above;
            if (top + it.Above + host.ActualHeight > 0) return (it.Index, -top);
        }
        return (0, 0);
    }

    /// Scrolled back to a Place (the block drawn again may be another height: as near as it goes).
    public void GoTo((int Block, double Into) place)
    {
        if (scroller is null || items.Count == 0 || Panel() is not VirtualizingStackPanel panel) return;
        int index = Math.Clamp(place.Block, 0, items.Count - 1);
        panel.BringIndexIntoViewPublic(index);
        for (int round = 0; round < 3; round++) // the guessed heights above it settle as blocks are laid out
        {
            UpdateLayout();
            var (host, it) = Shown().FirstOrDefault(s => s.it.Index == index);
            if (host is null) return;
            double off = host.TranslatePoint(new Point(0, 0), scroller).Y - it.Above + place.Into;
            if (Math.Abs(off) < 0.5) return;
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + off);
        }
    }

    /// The blocks shown now (tests: the checklist boxes in them).
    internal IEnumerable<Block> Blocks => items.Select(i => i.Block);

    /// The space above each block (tests).
    internal IEnumerable<double> Gaps => items.Select(i => i.Above);

    /// QA (MdBench): the first checklist box clicked.
    internal bool TickForQa()
    {
        static IEnumerable<FrameworkElement> Spots(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Section s)
                    foreach (var b in Spots(s.Blocks)) yield return b;
                else if (block is Paragraph p && p.Inlines.FirstInline is InlineUIContainer { Child: Grid { Children: [Border, ..] } spot })
                    yield return spot;
            }
        }
        if (Spots(Blocks).FirstOrDefault() is not { } first) return false;
        first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
        return true;
    }

    /// `blocks` (Markdown.BlocksOf, at `size`) shown in place of what was; nothing stays selected.
    public void Show(List<Block> blocks, double size)
    {
        var list = new List<Item>(blocks.Count);
        double under = 0; // the bottom margin of the block before
        for (int k = 0; k < blocks.Count; k++)
        {
            var b = blocks[k];
            var m = b.Margin;
            double above = k == 0 ? Top : Math.Max(under, m.Top);
            under = m.Bottom;
            b.Margin = new Thickness(m.Left, 0, m.Right, 0);
            list.Add(new Item(k, b, ReadText.Doc(size, b), above, k == blocks.Count - 1 ? Bottom : 0));
        }
        items = list;
        pressed = -1;
        StopDrag();
        readingWidth = ReadingEms * size;
        if (presenter is not null) presenter.MaxWidth = readingWidth;
        swapping = true;
        try
        {
            ItemsSource = list;
        }
        finally
        {
            swapping = false;
        }
        anchor = end = null;
    }

    // ── the boxes ───────────────────────────────────────────────────────────────────────────────

    /// A block's place in the list: a ReadText showing the block's document, handed to the next block that scrolls in.
    private sealed class BlockHost : Border
    {
        public ReadText? Text { get; private set; }

        /// Its box has had the keyboard since it got this document (see Prime); a priming is on its way.
        public bool Primed, Priming;

        public BlockHost() =>
            // a box being clicked or selected in asks to be scrolled to: the view scrolls only when asked (the wheel,
            // the keys, a drag past the edge)
            AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) => e.Handled = true));

        public void Show(FlowDocument doc)
        {
            if (Text is null)
            {
                Text = new ReadText(doc) { IsInactiveSelectionHighlightEnabled = true };
                Child = Text;
            }
            else Text.Document = doc;
            Primed = false;
            doc.PagePadding = new Thickness(0);
            // a selection over several boxes looks the same in each, the focused one or not: the theme's colour now, a
            // copy (a brush in Resources gets frozen, and the palette's own are changed in place on a theme switch)
            Text.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Theme.P.Selection.Color);
        }

        /// The document let go: it may show in another box later.
        public void Release()
        {
            if (Text is not null) Text.Document = new FlowDocument();
        }
    }

    protected override DependencyObject GetContainerForItemOverride() => new BlockHost();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is BlockHost;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is not BlockHost host || item is not Item it) return;
        host.Margin = new Thickness(0, it.Above, 0, it.Below);
        host.Show(it.Doc);
        Paint(host, it);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is BlockHost host && item is Item it)
        {
            Keep(host, it); // Shift+arrows may have moved its selection
            if (host.IsKeyboardFocusWithin) Focus(); // the keyboard stays in the view
            host.Release();
        }
        base.ClearContainerForItemOverride(element, item);
    }

    private Panel? Panel() => presenter is not null && VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as Panel : null;

    /// The boxes on screen with their blocks, top to bottom.
    private IEnumerable<(BlockHost host, Item it)> Shown()
    {
        if (Panel() is not { } panel) yield break;
        foreach (UIElement child in panel.Children)
            if (child is BlockHost host && ItemContainerGenerator.ItemFromContainer(host) is Item it) yield return (host, it);
    }

    // ── the selection ───────────────────────────────────────────────────────────────────────────

    /// The selection's ends in document order, or null.
    private (Spot from, Spot to)? Span() => anchor is { } a && end is { } e ? (a.CompareTo(e) <= 0 ? (a, e) : (e, a)) : null;

    private static TextPointer At(FlowDocument doc, int offset) => doc.ContentStart.GetPositionAtOffset(offset) ?? doc.ContentEnd;

    private static int OffsetOf(FlowDocument doc, TextPointer p) => doc.ContentStart.GetOffsetToPosition(p);

    private static int EndOf(FlowDocument doc) => OffsetOf(doc, doc.ContentEnd);

    /// A box's selection drawn as the view's selection has it.
    private void Paint(BlockHost host, Item it)
    {
        if (host.Text is not { } rt) return;
        var doc = it.Doc;
        if (Span() is not (Spot from, Spot to) || it.Index < from.Block || it.Index > to.Block)
        {
            if (!rt.Selection.IsEmpty) rt.Selection.Select(doc.ContentStart, doc.ContentStart);
            return;
        }
        TextPointer start = it.Index == from.Block ? At(doc, from.At) : doc.ContentStart, stop = it.Index == to.Block ? At(doc, to.At) : doc.ContentEnd;
        if (rt.Selection.Start.CompareTo(start) != 0 || rt.Selection.End.CompareTo(stop) != 0) rt.Selection.Select(start, stop); // a drag's every move
        if (host.Primed || host.Priming || rt.IsKeyboardFocused) return;
        host.Priming = true;
        Dispatcher.BeginInvoke(() => Prime(host), DispatcherPriority.Loaded); // not in the middle of laying out
    }

    /// A box draws its selection while another element has the keyboard only once it has had the keyboard itself since
    /// it got its document (WPF puts the selection's highlight in then; IsInactiveSelectionHighlightEnabled alone draws
    /// nothing): it gets it for a moment, and it goes back where it was.
    private void Prime(BlockHost host)
    {
        host.Priming = false;
        if (host.Primed || host.Text is not { Selection.IsEmpty: false } rt || Window.GetWindow(this) is not { IsActive: true }) return;
        var back = Keyboard.FocusedElement;
        if (!rt.Focus()) return;
        host.Primed = true;
        if (back is not null && back != rt) back.Focus();
        else Focus();
    }

    private void PaintAll()
    {
        foreach (var (host, it) in Shown()) Paint(host, it);
    }

    /// A box's own selection (a word double-clicked, Shift+arrows) taken as the view's, while nothing runs over blocks.
    private void Keep(BlockHost host, Item it)
    {
        if (swapping || (anchor is { } a && end is { } e && a.Block != e.Block)) return;
        if (host.Text is not { } rt || rt.Document != it.Doc) return;
        if (rt.Selection.IsEmpty)
        {
            if (anchor?.Block == it.Index) anchor = end = null;
            return;
        }
        anchor = new Spot(it.Index, OffsetOf(it.Doc, rt.Selection.Start));
        end = new Spot(it.Index, OffsetOf(it.Doc, rt.Selection.End));
    }

    private void KeepShown()
    {
        foreach (var (host, it) in Shown()) Keep(host, it);
    }

    /// The selection from `from` to `to` (either order), drawn.
    internal void Select(int fromBlock, int fromAt, int toBlock, int toAt)
    {
        anchor = new Spot(fromBlock, fromAt);
        end = new Spot(toBlock, toAt);
        PaintAll();
    }

    /// QA: what each box on screen has selected ("·" for nothing), top to bottom.
    internal string PaintedForQa() => string.Join(" | ", Shown().Select(s => s.host.Text is { Selection.IsEmpty: false } rt ? ReadText.AsCopied(rt.Selection.Text) : "·"));

    public void SelectEverything()
    {
        if (items.Count == 0) return;
        Select(0, 0, items.Count - 1, EndOf(items[^1].Doc));
        Focus();
    }

    /// The selection as plain text: each block's part as ReadText copies it, a line break between blocks.
    internal string SelectedText()
    {
        KeepShown();
        if (Span() is not (Spot from, Spot to)) return "";
        var parts = new List<string>();
        for (int k = from.Block; k <= to.Block && k < items.Count; k++)
        {
            var doc = items[k].Doc;
            string part = ReadText.AsCopied(new TextRange(k == from.Block ? At(doc, from.At) : doc.ContentStart, k == to.Block ? At(doc, to.At) : doc.ContentEnd).Text);
            if (part.Length > 0) parts.Add(part);
        }
        return string.Join("\r\n", parts);
    }

    public void CopySelection() => ReadText.CopyOut(this, SelectedText());

    // ── the mouse ───────────────────────────────────────────────────────────────────────────────

    /// The box `source` is in, and whether it is the scroll bar instead.
    private (BlockHost? host, bool bar) Under(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != this; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is BlockHost host) return (host, false);
            if (d is ScrollBar) return (null, true);
        }
        return (null, false);
    }

    /// The place in the text nearest to `p` (this view's coordinates): in the box under it, the start of the block
    /// below a gap, the first shown block's start above them all and the last one's end below them all.
    private Spot? Near(Point p)
    {
        (BlockHost host, Item it)? last = null;
        foreach (var (host, it) in Shown())
        {
            double top = host.TranslatePoint(new Point(0, 0), this).Y;
            if (p.Y < top) return new Spot(it.Index, 0);
            last = (host, it);
            if (p.Y > top + host.ActualHeight) continue;
            if (host.Text is not { } rt || rt.Document != it.Doc) return new Spot(it.Index, 0);
            var at = rt.GetPositionFromPoint(TranslatePoint(p, rt), true);
            return new Spot(it.Index, at is null ? 0 : OffsetOf(it.Doc, at));
        }
        return last is { } l ? new Spot(l.it.Index, EndOf(l.it.Doc)) : null;
    }

    private bool Outside(Point p) => p.Y < 0 || p.Y > ActualHeight;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        StopDrag();
        var (host, bar) = Under(e.OriginalSource);
        if (bar || e.ClickCount > 1 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return; // the box's own word, line, Shift+click
        var p = e.GetPosition(this);
        anchor = end = null;
        foreach (var (h, it) in Shown())
            if (h != host) Paint(h, it);
        if (host is not null && ItemContainerGenerator.ItemFromContainer(host) is Item pressedItem)
        {
            // the box selects (and its links and boxes take the click) until the drag leaves it
            pressed = pressedItem.Index;
            pressAt = Near(p) ?? new Spot(pressed, 0);
            return;
        }
        // between blocks or beside them: this view selects from the start
        if (Near(p) is not { } spot) return;
        Focus();
        anchor = end = spot;
        dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            pressed = -1;
            return;
        }
        var p = e.GetPosition(this);
        if (dragging)
        {
            DragTo(p);
            return;
        }
        if (pressed < 0 || Near(p) is not { } spot || (spot.Block == pressed && !Outside(p))) return;
        // the drag left its block: from here this view selects (the box lets go of the mouse and stops selecting)
        anchor = pressAt;
        pressed = -1;
        dragging = true;
        CaptureMouse();
        DragTo(p);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (dragging)
        {
            StopDrag();
            e.Handled = true;
            return;
        }
        if (pressed < 0) return;
        pressed = -1;
        Dispatcher.BeginInvoke(KeepShown, DispatcherPriority.Input); // the box's own selection, once it has made it
    }

    // a right press keeps the selection for the menu's 복사 (the box would move its caret there)
    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonDown(e);
        if (Span() is not null) e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (dragging && Mouse.Captured != this) StopDrag();
    }

    private void DragTo(Point p)
    {
        if (Near(p) is { } spot) end = spot;
        PaintAll();
        if (Outside(p) && CanCarry(p)) edge.Start();
        else edge.Stop();
    }

    /// There is more of the document past the edge the drag is held beyond.
    private bool CanCarry(Point p) => scroller is not null && (p.Y < 0 ? scroller.VerticalOffset > 0 : scroller.VerticalOffset < scroller.ScrollableHeight);

    /// A drag held above or below the view: it scrolls on, faster the further out, and the selection follows.
    private void Carry()
    {
        if (!dragging || scroller is null)
        {
            edge.Stop();
            return;
        }
        var p = Mouse.GetPosition(this);
        double past = p.Y < 0 ? p.Y : p.Y > ActualHeight ? p.Y - ActualHeight : 0;
        if (past == 0 || !CanCarry(p))
        {
            edge.Stop();
            return;
        }
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset + Math.Clamp(past, -80, 80) / 2 + Math.Sign(past) * 4);
        UpdateLayout(); // the blocks scrolled in are there to find the place in
        DragTo(p);
    }

    private void StopDrag()
    {
        edge.Stop();
        bool was = dragging;
        dragging = false;
        if (was && IsMouseCaptured) ReleaseMouseCapture();
    }

    // ── the keys ────────────────────────────────────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            base.OnPreviewKeyDown(e); // Alt+Space, Alt+arrows …: the window's, not scrolling
            return;
        }
        Key key = e.Key;
        if (ReadText.IsCopyKey(ctrl, key))
        {
            CopySelection();
            e.Handled = true;
            return;
        }
        if (ctrl && key == Key.A)
        {
            SelectEverything();
            e.Handled = true;
            return;
        }
        if (shift || scroller is null)
        {
            base.OnPreviewKeyDown(e); // Shift+arrows: the box selects in its block
            return;
        }
        switch (key)
        {
            case Key.Up:
                scroller.LineUp();
                break;
            case Key.Down:
                scroller.LineDown();
                break;
            case Key.PageUp:
                scroller.PageUp();
                break;
            case Key.PageDown or Key.Space:
                scroller.PageDown();
                break;
            case Key.Home:
                scroller.ScrollToTop();
                break;
            case Key.End:
                scroller.ScrollToBottom();
                break;
            default:
                base.OnPreviewKeyDown(e);
                return;
        }
        e.Handled = true;
    }
}
