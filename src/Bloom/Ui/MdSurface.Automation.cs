using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Bloom.Core;

namespace Bloom.Ui;

/// MdSurface for screen readers (UI Automation): a document named by its first heading (or first text) whose value is
/// its whole text, read-only; the links on screen (Invoke: opened) and checklist boxes on screen (Toggle: ticked) as its
/// children.
internal sealed partial class MdSurface
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// The whole text as copied.
    internal string AllText()
    {
        if (boxes.Count == 0) return "";
        var (a, b) = (selA, selB);
        selA = StartOf(0);
        selB = EndOf(boxes.Count - 1);
        string all = SelectedText();
        (selA, selB) = (a, b);
        return all;
    }

    private string Title()
    {
        var first = boxes.FirstOrDefault(b => b.Block.Kind == MdKind.Heading) ?? boxes.FirstOrDefault(b => b.Block.Spans.Count > 0);
        return first is null ? "" : string.Concat(first.Block.Spans.Select(s => s.Text)).Trim();
    }

    /// The links and checklist boxes on screen, with where they are (this element's coordinates).
    private IEnumerable<(object Target, Rect At, MdBox Box)> OnScreen()
    {
        if (env is null || boxes.Count == 0) yield break;
        for (int k = BlockAt(offset); k < boxes.Count && tops[k] < offset + viewport.Height; k++)
        {
            var b = boxes[k];
            if (!b.Shaped || b.Width != column) continue;
            double dx = left, dy = tops[k] - offset;
            var seen = new HashSet<MdLink>();
            foreach (var p in b.Parts)
                foreach (var (s, e, link) in p.Text.Links)
                {
                    if (!seen.Add(link)) continue;
                    var r = p.Rects(s, e).FirstOrDefault();
                    r.Offset(dx, dy);
                    yield return (link, r, b);
                }
            foreach (var spot in b.Spots)
                if (spot.Owner.Block.Kind == MdKind.Item)
                {
                    var r = spot.R;
                    r.Offset(dx, dy);
                    yield return (spot, r, b);
                }
        }
    }

    private sealed class Peer(MdSurface owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        private readonly Dictionary<object, AutomationPeer> kids = [];

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

        protected override string GetClassNameCore() => nameof(MdView);

        protected override string GetNameCore()
        {
            string set = base.GetNameCore();
            return set.Length > 0 ? set : owner.Title();
        }

        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Value ? this : base.GetPattern(pattern);

        protected override List<AutomationPeer> GetChildrenCore()
        {
            var list = new List<AutomationPeer>();
            var now = new HashSet<object>();
            foreach (var (target, at, box) in owner.OnScreen())
            {
                now.Add(target);
                if (!kids.TryGetValue(target, out var peer)) kids[target] = peer = new Child(owner, target, box);
                ((Child)peer).At = at;
                list.Add(peer);
            }
            foreach (var gone in kids.Keys.Where(k => !now.Contains(k)).ToList()) kids.Remove(gone);
            return list;
        }

        public bool IsReadOnly => true;

        public string Value => owner.AllText();

        public void SetValue(string value) => throw new InvalidOperationException("read-only");
    }

    /// A link (Invoke) or a checklist box (Toggle) on screen.
    private sealed class Child(MdSurface owner, object target, MdBox box) : AutomationPeer, IInvokeProvider, IToggleProvider
    {
        public Rect At;

        private bool IsLink => target is MdLink;

        public override object? GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.Invoke && IsLink ? this : pattern == PatternInterface.Toggle && !IsLink ? this : null;

        public void Invoke()
        {
            if (target is MdLink link) owner.OpenLink(link.Url);
        }

        public ToggleState ToggleState => target is MdSpot s && s.Owner.Block.Done == true ? ToggleState.On : ToggleState.Off;

        public void Toggle()
        {
            if (target is MdSpot s && !owner.TickBox(s.Owner)) throw new ElementNotEnabledException();
        }

        protected override string GetNameCore()
        {
            if (target is MdLink link)
            {
                var text = new StringBuilder();
                foreach (var p in box.Parts)
                    foreach (var (s, e, l) in p.Text.Links)
                        if (l == link) text.Append(p.Text.Copy(s, e));
                return text.ToString();
            }
            return target is MdSpot spot ? string.Concat(spot.Owner.Block.Spans.Select(s => s.Text)).Trim() : "";
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => IsLink ? AutomationControlType.Hyperlink : AutomationControlType.CheckBox;

        protected override Rect GetBoundingRectangleCore()
        {
            if (PresentationSource.FromVisual(owner) is null) return Rect.Empty;
            var a = owner.PointToScreen(At.TopLeft);
            var b = owner.PointToScreen(At.BottomRight);
            return new Rect(a, b);
        }

        protected override Point GetClickablePointCore()
        {
            var r = GetBoundingRectangleCore();
            return r.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        protected override bool IsOffscreenCore() => false;

        protected override bool IsEnabledCore() => IsLink || owner.Toggle is not null;

        protected override string GetAcceleratorKeyCore() => "";
        protected override string GetAccessKeyCore() => "";
        protected override string GetAutomationIdCore() => "";
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override string GetClassNameCore() => IsLink ? "Hyperlink" : "CheckBox";
        protected override string GetHelpTextCore() => "";
        protected override string GetItemStatusCore() => "";
        protected override string GetItemTypeCore() => "";
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
        protected override bool HasKeyboardFocusCore() => false;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsKeyboardFocusableCore() => false;
        protected override bool IsPasswordCore() => false;
        protected override bool IsRequiredForFormCore() => false;
        protected override void SetFocusCore() { }
    }
}
