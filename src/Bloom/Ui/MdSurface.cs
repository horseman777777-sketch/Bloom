using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bloom.Core;

namespace Bloom.Ui;

/// A place in the view's text: a block, a text in it (MdBox.Parts), a place in that text.
internal readonly record struct MdSpotAt(int Block, int Part, int At) : IComparable<MdSpotAt>
{
    public int CompareTo(MdSpotAt o) => Block != o.Block ? Block.CompareTo(o.Block) : Part != o.Part ? Part.CompareTo(o.Part) : At.CompareTo(o.At);

    public static bool operator <(MdSpotAt a, MdSpotAt b) => a.CompareTo(b) < 0;

    public static bool operator >(MdSpotAt a, MdSpotAt b) => a.CompareTo(b) > 0;

    public static bool operator <=(MdSpotAt a, MdSpotAt b) => a.CompareTo(b) <= 0;

    public static bool operator >=(MdSpotAt a, MdSpotAt b) => a.CompareTo(b) >= 0;
}

/// MdView's content: the blocks (MdBox) one under another, the space between two the larger of their margins (12 above
/// the first, 14 under the last), in a column of the reading width (54 × the text size) in the middle — scrolled by the
/// ScrollViewer around it through IScrollInfo. Each block's top is the sum of the heights above it, so a place (a
/// block and pixels into it) is exact. Blocks are shaped when they come near the screen and the rest in slices of at
/// most 8 ms while the UI thread is otherwise idle; a block not shaped yet has a guessed height, and when it becomes
/// exact above the place on screen the offset moves with it, so what is on screen stays. Only what is near the screen is
/// drawn: a DrawingVisual per block (per 320 px band of a tall one) — the screen's at once, a screen above and below
/// while the UI thread is idle — recorded once and moved by a transform; the selection is drawn under them in a visual
/// of its own, a hovered link's underline and a hovered box's ring over them.
internal sealed partial class MdSurface : FrameworkElement, IScrollInfo
{
    public const double ReadingEms = 54, Top = 12, Bottom = 14, SideLeft = 16, SideRight = 14, Band = 320;
    private const double SliceMs = 8;

    private readonly VisualCollection visuals;
    private readonly DrawingVisual selectionLayer = new(), hoverLayer = new();
    private readonly Dictionary<(MdBox Box, int Band), DrawingVisual> realized = [];
    private List<MdBox> boxes = [];
    private readonly Dictionary<MdBox, int> indexOf = [];
    private string? lastMd;
    private bool lastDocument;
    private string[] lastLines = [];
    private List<MdBlock> lastBlocks = [];
    private double[] tops = []; // each block's top (its text, under its gap)
    private double extent;
    private MdEnv? env;
    private double size = 14, column, left, offset;
    private bool document;
    private string? baseDir;
    private (int Block, double Into) anchor;
    private Size viewport;
    private bool sliceQueued;
    private int scan; // where the slices look for work next
    private readonly Stopwatch sliceWatch = new();

    /// A link clicked (the host opens it).
    public Action<string> OpenLink = _ => { };

    /// A checklist box clicked: its item's line in the source; true when the source took it. Null: boxes take no clicks.
    public Func<int, bool>? Toggle;

    /// QA: the longest idle slice so far (ms).
    public double LongestSliceMs { get; private set; }

    /// QA: how much of that slice the GC held.
    public double LongestSliceGcMs { get; private set; }

    /// QA: from the last Show to the end of the layout that drew it (ms).
    public double ShowToDrawnMs { get; private set; }

    private readonly Stopwatch shown = new();

    public MdSurface()
    {
        visuals = new VisualCollection(this) { selectionLayer, hoverLayer };
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            Theme.Changed -= Repaint;
            Theme.Changed += Repaint;
            MdPictures.Open(this);
        };
        Unloaded += (_, _) =>
        {
            Theme.Changed -= Repaint;
            MdPictures.Closed(this);
        };
        edge.Tick += (_, _) => Carry();
        copiedTimer.Tick += (_, _) => CopiedOver();
    }

    protected override int VisualChildrenCount => visuals.Count;

    protected override Visual GetVisualChild(int index) => visuals[index];

    /// The blocks shown (tests: their model).
    internal IReadOnlyList<MdBox> Boxes => boxes;

    public double TextSize => size;

    // ── what is shown ──────────────────────────────────────────────────────────────────────────

    /// `md` drawn in place of what was (at `size`; `document`: a .md file's look), nothing selected. Blocks whose
    /// source didn't change keep their shaping and drawing.
    public void Show(string md, double size, bool document, string? baseDir)
    {
        shown.Restart();
        MdPictures.LookAgain(); // a picture that couldn't be read may be there now
        // the same text again (another text size): read once
        if (md != lastMd || document != lastDocument)
        {
            lastLines = md.Replace("\r\n", "\n").Split('\n');
            lastBlocks = MdParser.Parse(md, document, true);
            lastMd = md;
            lastDocument = document;
        }
        var lines = lastLines;
        var blocks = lastBlocks;
        bool same = env is not null && size == this.size && document == this.document && baseDir == this.baseDir;
        var pool = new Dictionary<string, List<MdBox>>();
        if (same)
            foreach (var b in boxes)
            {
                if (!pool.TryGetValue(b.Key, out var q)) pool[b.Key] = q = [];
                q.Add(b);
            }
        else env = null;
        this.size = size;
        this.document = document;
        this.baseDir = baseDir;
        var list = new List<MdBox>(blocks.Count);
        foreach (var b in blocks)
        {
            string key = string.Join('\n', lines, b.Line, Math.Max(0, Math.Min(b.End, lines.Length) - b.Line));
            // the first box of the same source drawn as this reading has it (a box ticked since shows its own state)
            int at = pool.TryGetValue(key, out var q) ? q.FindIndex(x => x.Shows(b)) : -1;
            if (at >= 0)
            {
                var kept = q![at];
                q.RemoveAt(at);
                kept.Adopt(b);
                if (kept.MissesPictures()) kept.Invalidate();
                list.Add(kept);
            }
            else list.Add(new MdBox(b, key));
        }
        boxes = list;
        indexOf.Clear();
        for (int k = 0; k < list.Count; k++) indexOf[list[k]] = k;
        selA = selB = null;
        hover = null;
        pressed = null;
        StopDrag();
        if (env is not null)
            foreach (var b in boxes)
            {
                b.Margins(env.M);
                Guess(b);
            }
        Relayout();
        InvalidateMeasure();
        InvalidateArrange();
        Schedule();
    }

    /// Drawn again from scratch: the theme changed.
    private void Repaint()
    {
        DropAll();
        InvalidateArrange();
    }

    private void DropAll()
    {
        foreach (var v in realized.Values) visuals.Remove(v);
        realized.Clear();
    }

    private void Forget(MdBox box)
    {
        foreach (var key in realized.Keys.Where(k => k.Box == box).ToList())
        {
            visuals.Remove(realized[key]);
            realized.Remove(key);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        env = null; // every text shaped again at the new DPI
        DropAll();
        InvalidateArrange();
    }

    private MdEnv Env()
    {
        if (env is null)
        {
            env = new MdEnv(size, document, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            foreach (var b in boxes)
            {
                b.Shaped = false;
                b.Margins(env.M);
                Guess(b);
            }
            DropAll();
            Relayout(); // the guessed heights in place: only the blocks on screen are laid out now (Near)
        }
        return env;
    }

    // ── layout ──────────────────────────────────────────────────────────────────────────────────

    /// A block's height: as laid out (at this width, or until it is laid out again at another one), else guessed.
    private double HeightOf(MdBox b) => b.Width > 0 ? b.Height : b.Estimated >= 0 ? b.Estimated : 0;

    /// A block not laid out yet given a guessed height (once the text size and the column are known).
    private void Guess(MdBox b)
    {
        if (env is not null && column > 0 && b.Width < 0 && b.Estimated < 0) b.Estimated = b.Estimate(env.M, column);
    }

    /// The space above block k (its gap).
    private double Above(int k) => k == 0 ? Top : Math.Max(boxes[k - 1].MarginBottom, boxes[k].MarginTop);

    /// The space above each block (tests).
    internal IEnumerable<double> Gaps => Enumerable.Range(0, boxes.Count).Select(Above);

    /// Every block's top again from the heights, and the offset kept at the place on screen.
    private void Relayout()
    {
        int n = boxes.Count;
        if (tops.Length != n) tops = new double[n];
        double y = 0;
        for (int k = 0; k < n; k++)
        {
            y += Above(k);
            tops[k] = y;
            y += HeightOf(boxes[k]);
        }
        extent = n == 0 ? 0 : y + Bottom;
        if (n > 0)
        {
            int a = Math.Clamp(anchor.Block, 0, n - 1);
            offset = Clamp(tops[a] - Above(a) + anchor.Into);
        }
        else offset = 0;
    }

    private double Clamp(double o) => Math.Max(0, Math.Min(o, extent - viewport.Height));

    /// The first block whose slot (with its gap) reaches below `y`.
    private int BlockAt(double y)
    {
        int lo = 0, hi = boxes.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (tops[mid] + HeightOf(boxes[mid]) <= y) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// The place on screen kept: the first block at the top and how far into its slot the top is.
    private void KeepAnchor()
    {
        if (boxes.Count == 0)
        {
            anchor = (0, 0);
            return;
        }
        int k = BlockAt(offset);
        anchor = (k, offset - (tops[k] - Above(k)));
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 0 : available.Width, h = double.IsInfinity(available.Height) ? 0 : available.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        bool resized = final != viewport;
        viewport = final;
        Env();
        double room = Math.Max(1, final.Width - SideLeft - SideRight);
        double w = Math.Min(room, ReadingEms * size);
        left = SideLeft + Math.Max(0, (room - w) / 2);
        if (w != column)
        {
            column = w;
            foreach (var b in boxes) Guess(b);
            Relayout();
            DropAll();
        }
        else if (resized) Relayout();
        Near();
        Realize();
        ScrollOwner?.InvalidateScrollInfo();
        Schedule();
        if (shown.IsRunning && column > 0)
        {
            ShowToDrawnMs = shown.Elapsed.TotalMilliseconds;
            shown.Reset();
        }
        return final;
    }

    /// The blocks on screen shaped and laid out now (the offset kept as they become exact); the idle slices do those
    /// around it next.
    private void Near()
    {
        if (boxes.Count == 0 || column <= 0) return;
        for (int round = 0; round < 6; round++)
        {
            int from = BlockAt(Math.Max(0, offset)), to = BlockAt(offset + viewport.Height);
            bool any = false;
            for (int k = from; k <= to; k++) any |= Ready(k);
            if (!any) return;
            Relayout();
        }
    }

    /// Block k shaped and laid out at the column's width; false if it already was.
    private bool Ready(int k)
    {
        var b = boxes[k];
        if (b.Shaped && b.Width == column) return false;
        var e = Env();
        if (!b.Shaped)
        {
            b.Shape(e, Theme.P.Text, baseDir);
            AskPictures(b);
        }
        b.Arrange(e, column);
        Forget(b);
        return true;
    }

    /// The idle slices: blocks not shaped (or laid out at another width) yet, from the place on screen down, then from
    /// the top — at most SliceMs at a time, the UI thread's input first.
    private void Schedule()
    {
        if (sliceQueued || boxes.Count == 0 || column <= 0) return;
        sliceQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Slice);
    }

    private void Slice()
    {
        sliceQueued = false;
        if (boxes.Count == 0 || column <= 0 || !IsVisible && PresentationSource.FromVisual(this) is null) return;
        sliceWatch.Restart();
        var gc0 = GC.GetTotalPauseDuration();
        int n = boxes.Count, looked = 0;
        bool changed = false;
        // a screen above and below first (scrolling there finds them ready), then the rest
        for (int k = BlockAt(Math.Max(0, offset - viewport.Height)); k < n && tops[k] < offset + viewport.Height * 2 && sliceWatch.Elapsed.TotalMilliseconds < SliceMs; k++)
            changed |= Ready(k);
        if (scan >= n) scan = 0;
        while (looked < n && sliceWatch.Elapsed.TotalMilliseconds < SliceMs)
        {
            int k = (anchor.Block + scan) % n;
            scan++;
            looked++;
            if (scan >= n) scan = 0;
            if (Ready(k)) changed = true;
        }
        if (sliceWatch.Elapsed.TotalMilliseconds > LongestSliceMs)
        {
            LongestSliceMs = sliceWatch.Elapsed.TotalMilliseconds;
            LongestSliceGcMs = (GC.GetTotalPauseDuration() - gc0).TotalMilliseconds;
        }
        if (changed)
        {
            Relayout();
            InvalidateArrange();
            ScrollOwner?.InvalidateScrollInfo();
        }
        if (changed || looked >= n && boxes.Any(b => !b.Shaped || b.Width != column)) Schedule();
    }

    /// Whether every block is shaped and laid out (QA).
    internal bool Settled => boxes.All(b => b.Shaped && b.Width == column);

    private void AskPictures(MdBox b)
    {
        foreach (var p in b.Pictures())
        {
            if (p.Asked) continue;
            p.Asked = true;
            MdPictures.Read(p, Dispatcher, () =>
            {
                if (!boxes.Contains(b)) return;
                b.Invalidate(); // missing: its alt text instead; there: its room
                Forget(b);
                InvalidateArrange();
            });
        }
    }

    // ── drawing ─────────────────────────────────────────────────────────────────────────────────

    private double DeviceY(double y) => env?.Snap(y) ?? y;

    /// The bands of the blocks within a screen of the screen drawn (recorded once), the rest let go, each moved to its
    /// place; then the selection and the hover over them.
    private void Realize()
    {
        if (boxes.Count > 0 && column > 0)
        {
            // what is on screen now; a screen above and below while the UI thread is idle (Widen)
            bool all = Record(offset, offset + viewport.Height, double.PositiveInfinity);
            if (!all || !Record(offset - viewport.Height, offset + viewport.Height * 2, 0)) Widen(later: true);
            double from = offset - viewport.Height * 1.5, to = offset + viewport.Height * 2.5;
            List<MdBox>? gone = null;
            foreach (var key in realized.Keys.ToList())
            {
                var (box, band) = key;
                int k = indexOf.TryGetValue(box, out int at) ? at : -1;
                if (k < 0 || tops[k] + (band + 1) * Band < from || tops[k] + band * Band > to || !box.Shaped || box.Width != column)
                {
                    visuals.Remove(realized[key]);
                    realized.Remove(key);
                    (gone ??= []).Add(box);
                    continue;
                }
                var t = (TranslateTransform)realized[key].Transform;
                t.X = DeviceY(left);
                t.Y = DeviceY(tops[k]) - DeviceY(offset);
            }
            // a block off screen lets go of its lines as drawn
            if (gone is not null)
                foreach (var box in gone.Distinct())
                    if (!indexOf.ContainsKey(box) || !realized.Keys.Any(r => r.Box == box)) box.Release();
        }
        else DropAll();
        PaintSelection();
        PaintHover();
    }

    private bool widenQueued;

    /// The screen above and below recorded too, at most SliceMs at a time.
    private void Widen(bool later)
    {
        if (later)
        {
            if (widenQueued) return;
            widenQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Widen(false));
            return;
        }
        widenQueued = false;
        if (boxes.Count == 0 || column <= 0 || PresentationSource.FromVisual(this) is null) return;
        if (!Record(offset - viewport.Height, offset + viewport.Height * 2, SliceMs)) Widen(later: true);
        else InvalidateArrange(); // placed by the next arrange
    }

    /// The bands of the shaped blocks between `from` and `to` recorded (those not yet), until `budget` ms have gone (0:
    /// none recorded, only whether all are); true when all are.
    private bool Record(double from, double to, double budget)
    {
        var e = Env();
        var watch = budget > 0 && !double.IsPositiveInfinity(budget) ? System.Diagnostics.Stopwatch.StartNew() : null;
        for (int k = BlockAt(Math.Max(0, from)); k < boxes.Count && tops[k] < to; k++)
        {
            var b = boxes[k];
            if (!b.Shaped || b.Width != column) continue;
            int first = (int)Math.Max(0, Math.Floor((from - tops[k]) / Band)), last = (int)Math.Floor(Math.Max(0, Math.Min(b.Height, to - tops[k]) - 0.01) / Band);
            for (int band = first; band <= last; band++)
            {
                if (realized.ContainsKey((b, band))) continue;
                if (budget <= 0 || watch is not null && watch.Elapsed.TotalMilliseconds > budget) return false;
                Decode(b);
                var v = new DrawingVisual { Transform = new TranslateTransform(DeviceY(left), DeviceY(tops[k]) - DeviceY(offset)) };
                using (var dc = v.RenderOpen())
                {
                    double top = band * Band, bottom = Math.Min(b.Height, top + Band);
                    bool clip = b.Height > Band;
                    if (clip) dc.PushClip(new RectangleGeometry(new Rect(-SideLeft, top, column + SideLeft + SideRight, bottom - top)));
                    b.Draw(dc, e, top, bottom);
                    if (clip) dc.Pop();
                }
                realized[(b, band)] = v;
                visuals.Insert(visuals.Count - 1, v);
            }
        }
        return true;
    }

    /// A block's pictures decoded for the width they are drawn at (from the cache, or redrawn once decoded).
    private void Decode(MdBox b)
    {
        foreach (var p in b.Pictures())
            if (p.Ready && p.Width > 0)
                MdPictures.Decode(p, p.Width * (env?.Ppd ?? 1), Dispatcher, () =>
                {
                    if (!boxes.Contains(b)) return;
                    if (p.Missing) b.Invalidate();
                    Forget(b);
                    InvalidateArrange();
                });
    }

    /// A transparent face over all of it: the mouse finds the view between blocks too.
    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    // ── IScrollInfo ─────────────────────────────────────────────────────────────────────────────

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => viewport.Width;
    public double ExtentHeight => extent;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public ScrollViewer? ScrollOwner { get; set; }

    /// One line of the keys' scrolling, and a wheel notch's three (as the list this view replaced scrolled).
    private const double LinePx = 16;

    public void LineUp() => SetVerticalOffset(offset - LinePx);
    public void LineDown() => SetVerticalOffset(offset + LinePx);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(offset - SystemParameters.WheelScrollLines * LinePx);
    public void MouseWheelDown() => SetVerticalOffset(offset + SystemParameters.WheelScrollLines * LinePx);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double o) { }

    public void SetVerticalOffset(double o)
    {
        double to = Clamp(double.IsPositiveInfinity(o) ? extent : o);
        if (to == offset && !double.IsPositiveInfinity(o)) return;
        offset = to;
        KeepAnchor();
        if (double.IsPositiveInfinity(o) && boxes.Count > 0) anchor = (boxes.Count - 1, double.MaxValue / 4); // the end, wherever it turns out to be
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    /// Where it is scrolled to: the first block on screen and how many pixels of its slot (with the space above it) are
    /// above the top.
    public (int Block, double Into) Place()
    {
        if (boxes.Count == 0) return (0, 0);
        int k = BlockAt(offset);
        return (k, offset - (tops[k] - Above(k)));
    }

    /// Scrolled back to a Place (exact: the blocks above it are measured or move the offset when they are).
    public void GoTo((int Block, double Into) place)
    {
        if (boxes.Count == 0) return;
        anchor = (Math.Clamp(place.Block, 0, boxes.Count - 1), place.Into);
        Relayout();
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }
}
