using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bloom.Core;

namespace Bloom.Ui;

/// MdSurface: the selection (two places, either order; drawn under the text), the mouse (a drag selects over blocks and
/// scrolls on past the top or bottom; a double-click a word, a triple-click a text; Shift+click extends; links,
/// checklist boxes and 복사 take a press and release on them), Shift+arrows, and copying.
internal sealed partial class MdSurface
{
    private MdSpotAt? selA, selB; // as made: selA where it began
    private object? hover; // the MdLink or MdSpot under the mouse
    private object? pressed; // a link or spot pressed, until the mouse moves off it
    private Point pressAt;
    private bool dragging;
    private readonly DispatcherTimer edge = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer copiedTimer = new() { Interval = MdMetrics.CopiedFor };
    private MdBox? copiedBox;

    /// The selection's ends in order, or null (nothing, or an empty one).
    private (MdSpotAt From, MdSpotAt To)? Span() => selA is { } a && selB is { } b && a != b ? (a < b ? (a, b) : (b, a)) : null;

    public bool HasSelection => Span() is not null;

    // ── places ──────────────────────────────────────────────────────────────────────────────────

    /// Block k's texts made from its block, not shaped or laid out (copying a block, moving over it, need only these).
    private MdBox Built(int k)
    {
        var b = boxes[k];
        b.Make(Env(), Theme.P.Text, baseDir);
        return b;
    }

    private MdSpotAt StartOf(int k) => new(k, 0, 0);

    private MdSpotAt EndOf(int k)
    {
        var b = Built(k);
        return b.Parts.Count == 0 ? new(k, 0, 0) : new(k, b.Parts.Count - 1, b.Parts[^1].Text.Text.Length);
    }

    /// The place nearest to `p` (this element's coordinates): in the block under it; below a gap the next block's start;
    /// above them all the first block's start, below them all the last one's end.
    private MdSpotAt? Nearest(Point p)
    {
        if (boxes.Count == 0) return null;
        double y = p.Y + offset, x = p.X - left;
        if (y < tops[0]) return StartOf(0);
        int k = BlockAt(y);
        if (k >= boxes.Count - 1 && y > tops[^1] + HeightOf(boxes[^1])) return EndOf(boxes.Count - 1);
        if (y < tops[k]) return StartOf(k);
        Ready(k);
        if (boxes[k].Hit(x, y - tops[k]) is { } hit) return new MdSpotAt(k, hit.Part, hit.At);
        return k + 1 < boxes.Count ? StartOf(k + 1) : EndOf(k); // a rule
    }

    /// Where a place is drawn (this element's coordinates): its x, its line's top and height.
    internal Rect BoundsOf(MdSpotAt s)
    {
        var b = boxes[s.Block];
        Ready(s.Block);
        if (b.Parts.Count == 0) return new Rect(left, tops[s.Block] - offset, 0, b.Height);
        var r = b.Parts[s.Part].Bounds(s.At);
        r.Offset(left, tops[s.Block] - offset);
        return r;
    }

    /// The place at `p` (tests: hit-testing).
    internal MdSpotAt? HitForQa(Point p) => Nearest(p);

    // ── the selection ───────────────────────────────────────────────────────────────────────────

    /// The selection from one place to another (either order), drawn.
    internal void Select(MdSpotAt from, MdSpotAt to)
    {
        selA = from;
        selB = to;
        PaintSelection();
    }

    public void SelectAll()
    {
        if (boxes.Count == 0) return;
        Select(StartOf(0), EndOf(boxes.Count - 1));
    }

    public void ClearSelection()
    {
        selA = selB = null;
        PaintSelection();
    }

    /// The selection as plain text: each text's part, cells in a row by tabs and rows by line breaks, other texts and
    /// blocks by line breaks; an empty block adds nothing.
    public string SelectedText()
    {
        if (Span() is not (MdSpotAt from, MdSpotAt to)) return "";
        var parts = new List<string>();
        for (int k = from.Block; k <= to.Block && k < boxes.Count; k++)
        {
            var b = Built(k);
            if (b.Parts.Count == 0) continue;
            int p0 = k == from.Block ? from.Part : 0, p1 = k == to.Block ? to.Part : b.Parts.Count - 1;
            var sb = new StringBuilder();
            for (int p = p0; p <= p1 && p < b.Parts.Count; p++)
            {
                var t = b.Parts[p].Text.Text;
                int a = k == from.Block && p == from.Part ? Math.Min(from.At, t.Length) : 0, z = k == to.Block && p == to.Part ? Math.Min(to.At, t.Length) : t.Length;
                sb.Append(b.Copy(p, a, Math.Max(a, z)));
                if (p < p1) sb.Append(b.Joiner(p));
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
        }
        return string.Join("\r\n", parts);
    }

    /// The selection drawn: rectangles under the selected text of the blocks near the screen.
    private void PaintSelection()
    {
        using var dc = selectionLayer.RenderOpen();
        if (Span() is not (MdSpotAt from, MdSpotAt to) || boxes.Count == 0) return;
        double top = offset - viewport.Height, bottom = offset + viewport.Height * 2;
        int first = Math.Max(from.Block, BlockAt(Math.Max(0, top))), last = to.Block;
        double x = DeviceY(left), o = DeviceY(offset);
        for (int k = first; k <= last && k < boxes.Count && tops[k] < bottom; k++)
        {
            var b = boxes[k];
            if (!b.Shaped || b.Width != column) continue;
            double y = DeviceY(tops[k]) - o;
            int p0 = k == from.Block ? from.Part : 0, p1 = k == to.Block ? to.Part : b.Parts.Count - 1;
            for (int p = p0; p <= p1 && p < b.Parts.Count; p++)
            {
                var part = b.Parts[p];
                int n = part.Text.Text.Length;
                int a = k == from.Block && p == from.Part ? from.At : 0, z = k == to.Block && p == to.Part ? to.At : n;
                bool all = a == 0 && z == n && n == 0;
                if (all) // an empty cell or line: a sliver, so it shows as taken
                    dc.DrawRectangle(Theme.P.Selection, null, new Rect(x + part.X, y + part.Y, part.LineHeight * 0.25, part.LineHeight));
                foreach (var r in part.Rects(a, Math.Min(z, n)))
                    dc.DrawRectangle(Theme.P.Selection, null, new Rect(x + r.X, y + r.Y, r.Width, r.Height));
            }
        }
    }

    /// The hovered link underlined, the hovered box ringed in the accent.
    private void PaintHover()
    {
        using var dc = hoverLayer.RenderOpen();
        if (hover is null || env is null || boxes.Count == 0) return;
        double x = DeviceY(left), o = DeviceY(offset);
        int first = BlockAt(Math.Max(0, offset)), last = BlockAt(offset + viewport.Height);
        for (int k = first; k <= last && k < boxes.Count; k++)
        {
            var b = boxes[k];
            if (!b.Shaped || b.Width != column) continue;
            double y = DeviceY(tops[k]) - o;
            if (hover is MdLink link)
                foreach (var r in b.Underlines(env, link))
                    dc.DrawRectangle(Theme.P.AccentText, null, new Rect(x + r.X, y + r.Y, r.Width, r.Height));
            else if (hover is MdSpot spot && b.Spots.Contains(spot) && spot.Owner.Block.Kind == MdKind.Item && spot.Owner.Block.Done == false && Toggle is not null)
            {
                dc.PushTransform(new TranslateTransform(x, y));
                MdBox.BoxShape(dc, env, spot.Inner, null, Theme.P.Accent);
                dc.Pop();
            }
        }
    }

    // ── the mouse ───────────────────────────────────────────────────────────────────────────────

    /// What takes a click at `p`: a checklist box or 복사 (MdSpot), a link (MdLink), or nothing.
    private object? TargetAt(Point p)
    {
        if (boxes.Count == 0) return null;
        double y = p.Y + offset, x = p.X - left;
        int k = BlockAt(y);
        if (k >= boxes.Count || y < tops[k] || y > tops[k] + HeightOf(boxes[k])) return null;
        var b = boxes[k];
        if (!b.Shaped || b.Width != column) return null;
        if (b.SpotAt(x, y - tops[k]) is { } spot) return spot.Owner.Block.Kind == MdKind.Item && Toggle is null ? null : spot;
        return b.LinkAt(x, y - tops[k])?.Link;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        StopDrag();
        ScrollOwner?.Focus(); // the view: Ctrl+C, Shift+arrows
        var p = e.GetPosition(this);
        pressed = TargetAt(p);
        pressAt = p;
        if (Nearest(p) is not { } at) return;
        e.Handled = true;
        if (e.ClickCount == 2 && boxes[at.Block].Parts.Count > 0)
        {
            var (s, z) = boxes[at.Block].Parts[at.Part].Text.WordAt(at.At);
            Select(at with { At = s }, at with { At = z });
            return;
        }
        if (e.ClickCount >= 3 && boxes[at.Block].Parts.Count > 0)
        {
            Select(at with { At = 0 }, at with { At = boxes[at.Block].Parts[at.Part].Text.Text.Length });
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && selA is not null) selB = at;
        else selA = selB = at;
        PaintSelection();
        dragging = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            if (pressed is not null && (p - pressAt).Length > 4) pressed = null; // a drag from a link selects
            if (pressed is null) DragTo(p);
            return;
        }
        var target = TargetAt(p);
        if (!Equals(target, hover))
        {
            hover = target;
            PaintHover();
        }
        Cursor = target is not null ? Cursors.Hand : p.X >= left - 4 && p.X <= left + column + 4 ? Cursors.IBeam : Cursors.Arrow;
        // a picture's alt text as its tooltip
        string? alt = null;
        double y = p.Y + offset;
        int k = boxes.Count > 0 ? BlockAt(y) : -1;
        if (k >= 0 && k < boxes.Count && boxes[k].Shaped && boxes[k].Width == column && boxes[k].PictureAt(p.X - left, y - tops[k]) is { Alt.Length: > 0 } picture) alt = picture.Alt;
        if (!Equals(ToolTip, alt)) ToolTip = alt;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover is null) return;
        hover = null;
        PaintHover();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var p = e.GetPosition(this);
        bool was = dragging;
        StopDrag();
        if (pressed is { } target && (p - pressAt).Length <= 4 && Equals(TargetAt(p), target))
        {
            pressed = null;
            if (selA == selB) selA = selB = null;
            PaintSelection();
            Activate(target);
            e.Handled = true;
            return;
        }
        pressed = null;
        if (was) e.Handled = true;
    }

    /// A press and release on a link (opened), a checklist box (ticked in the source, then this block drawn again) or
    /// 복사 (the code copied, 복사 완료 for a moment).
    private void Activate(object target)
    {
        if (target is MdLink link)
        {
            OpenLink(link.Url);
            return;
        }
        if (target is not MdSpot spot) return;
        var b = spot.Owner;
        if (b.Block.Kind == MdKind.Code)
        {
            Search.Launch.Copy(b.Block.Code);
            if (copiedBox is { } before && before != b) CopiedOver();
            copiedBox = b;
            b.Copied = true;
            Redraw(b);
            copiedTimer.Stop();
            copiedTimer.Start();
            return;
        }
        TickBox(b);
    }

    /// A checklist item's box clicked: the source takes it (Toggle) and the item is drawn again ticked or not.
    internal bool TickBox(MdBox item)
    {
        if (Toggle is null || item.Block.Done is not { } done || !Toggle(item.Block.Line)) return false;
        item.Block.Done = !done;
        lastMd = null; // that reading no longer is its source's: the next Show reads again (its box isn't reused: Show)
        item.Invalidate(); // its text dimmed or not
        Redraw(item);
        hover = null;
        PaintHover();
        return true;
    }

    private void CopiedOver()
    {
        copiedTimer.Stop();
        if (copiedBox is not { } b) return;
        copiedBox = null;
        b.Copied = false;
        Redraw(b);
    }

    /// One block (or the top-level block holding it) laid out and drawn again.
    private void Redraw(MdBox inner)
    {
        foreach (var b in boxes)
            if (b == inner || b.Contains(inner))
            {
                if (b != inner && !inner.Shaped) b.Invalidate(); // a quote holds its blocks' texts
                b.Width = -1;
                Forget(b);
            }
        InvalidateArrange();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true; // the selection stays for the menu's 복사
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (dragging && Mouse.Captured != this) StopDrag();
    }

    private void DragTo(Point p)
    {
        if (Nearest(p) is { } at) selB = at;
        PaintSelection();
        if (Outside(p) && CanCarry(p)) edge.Start();
        else edge.Stop();
    }

    private bool Outside(Point p) => p.Y < 0 || p.Y > viewport.Height;

    /// There is more past the edge the drag is held beyond.
    private bool CanCarry(Point p) => p.Y < 0 ? offset > 0 : offset < extent - viewport.Height;

    /// A drag held above or below the view: it scrolls on, faster the further out, and the selection follows.
    private void Carry()
    {
        if (!dragging)
        {
            edge.Stop();
            return;
        }
        var p = Mouse.GetPosition(this);
        double past = p.Y < 0 ? p.Y : p.Y > viewport.Height ? p.Y - viewport.Height : 0;
        if (past == 0 || !CanCarry(p))
        {
            edge.Stop();
            return;
        }
        SetVerticalOffset(offset + Math.Clamp(past, -80, 80) / 2 + Math.Sign(past) * 4);
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

    /// Shift+arrows, Shift+Home/End: the selection's moving end one character, one line or to its line's end — over
    /// texts and blocks — and the view scrolled to keep it on screen.
    public bool Extend(Key key)
    {
        if (boxes.Count == 0) return false;
        if (selB is not { } end)
        {
            if (Nearest(new Point(left, 0)) is not { } start) return false;
            selA = selB = end = start;
        }
        MdSpotAt? next = key switch
        {
            Key.Left => Step(end, -1),
            Key.Right => Step(end, +1),
            Key.Up or Key.Down => Vertical(end, key == Key.Up ? -1 : 1),
            Key.Home or Key.End => LineEdge(end, key == Key.End),
            _ => null,
        };
        if (next is not { } to) return false;
        selB = to;
        Reveal(to);
        PaintSelection();
        return true;
    }

    /// One grapheme cluster left or right, into the text before or after at its edge.
    private MdSpotAt Step(MdSpotAt s, int dir)
    {
        var b = Built(s.Block);
        if (b.Parts.Count > 0)
        {
            var t = b.Parts[s.Part].Text.Text;
            if (dir < 0 && s.At > 0) return s with { At = s.At - ClusterBefore(t, s.At) };
            if (dir > 0 && s.At < t.Length) return s with { At = s.At + Math.Max(1, System.Globalization.StringInfo.GetNextTextElementLength(t, s.At)) };
            if (dir < 0 && s.Part > 0) return new MdSpotAt(s.Block, s.Part - 1, b.Parts[s.Part - 1].Text.Text.Length);
            if (dir > 0 && s.Part + 1 < b.Parts.Count) return new MdSpotAt(s.Block, s.Part + 1, 0);
        }
        for (int k = s.Block + dir; k >= 0 && k < boxes.Count; k += dir)
            if (Built(k).Parts.Count > 0) return dir < 0 ? EndOf(k) : StartOf(k);
        return s;
    }

    private static int ClusterBefore(string t, int at)
    {
        int k = at - 1;
        var g = LineBreaks.Graphemes(t[..at]);
        while (k > 0 && !g[k]) k--;
        return at - k;
    }

    /// The place a line up or down at the same x.
    private MdSpotAt? Vertical(MdSpotAt s, int dir)
    {
        var r = BoundsOf(s);
        double y = dir < 0 ? r.Top - 1 : r.Bottom + 1;
        if (y < 0 || y > viewport.Height)
        {
            SetVerticalOffset(offset + (dir < 0 ? y - LinePx : y - viewport.Height + LinePx));
            UpdateLayout();
            r = BoundsOf(s);
            y = dir < 0 ? r.Top - 1 : r.Bottom + 1;
        }
        // over a gap between blocks: into the next block's first line or the last one's of the block before
        double at = y + offset;
        int k = BlockAt(at);
        if (k < boxes.Count && at < tops[k])
        {
            if (dir > 0) at = tops[k] + 1;
            else if (k > 0) at = tops[k - 1] + HeightOf(boxes[k - 1]) - 1;
        }
        return Nearest(new Point(r.X, at - offset));
    }

    /// The start or end of the line the place is on.
    private MdSpotAt LineEdge(MdSpotAt s, bool toEnd)
    {
        var b = Built(s.Block);
        Ready(s.Block);
        if (b.Parts.Count == 0) return s;
        var part = b.Parts[s.Part];
        int k = part.LineOf(s.At);
        return s with { At = toEnd ? LineBreaks.VisibleEnd(part.Text.Text, part.Starts[k], part.End(k)) : part.Starts[k] };
    }

    /// Scrolled so the place is on screen.
    private void Reveal(MdSpotAt s)
    {
        var r = BoundsOf(s);
        if (r.Top < 0) SetVerticalOffset(offset + r.Top);
        else if (r.Bottom > viewport.Height) SetVerticalOffset(offset + r.Bottom - viewport.Height);
    }

    // ── QA ──────────────────────────────────────────────────────────────────────────────────────

    /// QA (MdBench, tests): the n-th checklist box (top to bottom) clicked; false when there is none or it didn't take.
    internal bool TickForQa(int n = 0)
    {
        var items = new List<MdBox>();
        foreach (var b in boxes) b.Items(items);
        return n < items.Count && TickBox(items[n]);
    }

    /// QA: what the view has selected, as copied, or "" .
    internal string PaintedForQa() => SelectedText();
}
