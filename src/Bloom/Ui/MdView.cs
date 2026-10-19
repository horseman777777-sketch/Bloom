using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bloom.Search;

namespace Bloom.Ui;

/// MdPane's 보기: a Markdown text drawn by Bloom itself (MdSurface: the blocks Core\MdModel.cs reads, shaped with
/// TextFormatter and broken into lines by Core\LineBreaks.cs — no FlowDocument), in a scroll viewer with the thin bar.
/// A long file opens at once (only what is near the screen is shaped first, the rest while the UI thread is idle) and
/// follows a new width by arithmetic. A drag selects over blocks and table cells, scrolling on past the top or bottom;
/// Ctrl+C and the menu's 복사 copy it, Ctrl+A selects everything; Shift+arrows move the selection's end.
internal sealed class MdView : ScrollViewer, ISelectionOwner
{
    private readonly MdSurface surface = new();
    private PopupMenu? menu;

    public MdView()
    {
        Template = Launcher.ThinScroll();
        CanContentScroll = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = true;
        FocusVisualStyle = null;
        Content = surface;
    }

    /// The drawn text (tests, QA).
    internal MdSurface Surface => surface;

    /// Where a clicked link goes.
    public System.Action<string> OpenLink
    {
        set => surface.OpenLink = value;
    }

    /// A checklist box clicked: its item's source line; true when the source took it. Null: the boxes take no clicks.
    public System.Func<int, bool>? Toggle
    {
        set => surface.Toggle = value;
    }

    public void ScrollTo(double offset) => ScrollToVerticalOffset(offset);

    /// Where the view is scrolled to: the first block on screen, and how many pixels of it (with the space above it)
    /// are above the top.
    public (int Block, double Into) Place() => surface.Place();

    /// Scrolled back to a Place.
    public void GoTo((int Block, double Into) place) => surface.GoTo(place);

    /// `md` drawn at `size` in place of what was (`document`: a .md file's look; `baseDir`: where pictures' relative
    /// paths start); nothing stays selected.
    public void Show(string md, double size, bool document, string? baseDir) => surface.Show(md, size, document, baseDir);

    public void SelectEverything()
    {
        surface.SelectAll();
        Focus();
    }

    /// The selection as plain text (cells by tabs, rows and blocks by line breaks).
    internal string SelectedText() => surface.SelectedText();

    public void CopySelection() => ReadText.CopyOut(this, surface.SelectedText());

    /// QA (MdBench): the first checklist box clicked.
    internal bool TickForQa() => surface.TickForQa();

    // ── the menu ────────────────────────────────────────────────────────────────────────────────

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        menu ??= new PopupMenu();
        menu.Open(new[]
        {
            new ActionItem { Title = Tr("복사", "Copy"), Keys = "Ctrl+C", Glyph = G.Copy, Run = CopySelection },
            new ActionItem { Title = Tr("모두 선택", "Select all"), Keys = "Ctrl+A", Glyph = G.Page, Run = SelectEverything },
        });
    }

    // Windows' own menu would come up otherwise
    protected override void OnContextMenuOpening(ContextMenuEventArgs e) => e.Handled = true;

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
        if (shift && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            e.Handled = surface.Extend(key);
            return;
        }
        switch (key)
        {
            case Key.Up:
                LineUp();
                break;
            case Key.Down:
                LineDown();
                break;
            case Key.PageUp:
                PageUp();
                break;
            case Key.PageDown or Key.Space:
                PageDown();
                break;
            case Key.Home:
                ScrollToTop();
                break;
            case Key.End:
                ScrollToBottom();
                break;
            default:
                base.OnPreviewKeyDown(e);
                return;
        }
        e.Handled = true;
    }
}
