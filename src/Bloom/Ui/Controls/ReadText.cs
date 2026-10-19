using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Bloom.Core;
using Bloom.Search;

namespace Bloom.Ui;

/// The conversation a ReadText sits in (ChatView): where the keyboard goes back to and where "복사했어요" shows.
internal interface IComposerHost
{
    void FocusComposer();

    void ShowToast(string text);
}

/// A view made of many ReadTexts whose selection can run over several of them (MdView): copying and 모두 선택 are its.
internal interface ISelectionOwner
{
    void CopySelection();

    void SelectEverything();
}

/// Text in a conversation that can be dragged over and copied (Ctrl+C or the right button), across paragraphs, lists
/// and tables: a read-only RichTextBox with no frame, caret or scrolling of its own. Copying gives plain text without
/// the joiners Hangul.KeepAll puts between Korean letters. The wheel scrolls the conversation, not this; any key but a
/// copy or a selection key hands the keyboard back to the composer (the search line or the chat window's input), and
/// so does a click that selects nothing.
internal sealed class ReadText : RichTextBox
{
    private double? natural; // Plain(fit): the widest line's width, so a short message makes a narrow bubble
    private PopupMenu? menu;

    public ReadText(FlowDocument doc)
    {
        Document = doc;
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsDocumentEnabled = true; // links and the code blocks' 복사 take clicks
        IsUndoEnabled = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        Foreground = Theme.P.Text;
        SelectionBrush = Theme.P.Selection;
        SelectionOpacity = 1;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // RichTextBox gives its document 5 px each side once it is shown: none, so the text lines up with the rest
        Loaded += (_, _) => Document.PagePadding = new Thickness(0);
        DataObject.AddCopyingHandler(this, OnCopying);
    }

    /// One plain paragraph (a question, an error), Korean kept together on wrap; `fit` sizes it to its widest line.
    public static ReadText Plain(string text, double size, FontWeight weight, Brush? ink = null, bool fit = false)
    {
        var p = Paragraph(size, weight);
        if (ink is not null) p.Foreground = ink;
        string shown = Hangul.KeepAll(text.Replace("\r\n", "\n"));
        var lines = shown.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            p.Inlines.Add(new Run(lines[i]));
        }
        var rt = new ReadText(Doc(size, p));
        if (fit) rt.natural = lines.Max(l => Measure(l, size, weight)) + 6; // room for the selection's end and rounding
        return rt;
    }

    public static FlowDocument Doc(double size, params Block[] blocks)
    {
        var doc = new FlowDocument { FontFamily = Theme.Sans, FontSize = size, Foreground = Theme.P.Text, PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        foreach (var b in blocks) doc.Blocks.Add(b);
        return doc;
    }

    /// A paragraph the way Markdown.Para lays one out (line height 1.6 × the size).
    public static Paragraph Paragraph(double size, FontWeight weight) => new()
    {
        FontSize = size,
        FontWeight = weight,
        LineHeight = Math.Round(size * 1.6),
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static double Measure(string line, double size, FontWeight weight) =>
        new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Theme.Sans, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, null, TextFormattingMode.Display, 1).WidthIncludingTrailingWhitespace; // as the windows draw it

    protected override Size MeasureOverride(Size constraint)
    {
        if (natural is not { } w) return base.MeasureOverride(constraint);
        double width = Math.Min(constraint.Width, w);
        var s = base.MeasureOverride(new Size(width, constraint.Height));
        return new Size(width, s.Height);
    }

    // ── copying ─────────────────────────────────────────────────────────────────────────────────
    /// What a copy puts on the clipboard now.
    public string SelectedText => AsCopied(Selection.Text);

    /// A TextRange's text as a copy carries it: no Korean joiners, no line break at the end.
    public static string AsCopied(string text) => Hangul.Unjoin(text).TrimEnd('\r', '\n');

    /// The box's own copy (and dragging the selection out) would carry rich text with Bloom's colours and the Korean
    /// joiners: it is stopped, and CopyNow puts plain text on the clipboard instead.
    private static void OnCopying(object sender, DataObjectCopyingEventArgs e) => e.CancelCommand();

    private void CopyNow()
    {
        if (Above<ISelectionOwner>(this) is { } owner)
        {
            owner.CopySelection();
            return;
        }
        if (CopyOut(this, SelectedText)) Back();
    }

    /// `text` (unless empty) on the clipboard, and "복사 완료" from the conversation or pane `from` sits in.
    public static bool CopyOut(DependencyObject from, string text)
    {
        if (text.Length == 0) return false;
        Launch.Copy(text);
        Above<IComposerHost>(from)?.ShowToast(Tr("복사 완료", "Copied"));
        return true;
    }

    /// The copy keys: Ctrl+C, Ctrl+Insert.
    public static bool IsCopyKey(bool ctrl, Key key) => ctrl && key is Key.C or Key.Insert;

    // ── mouse and keys ──────────────────────────────────────────────────────────────────────────
    /// The conversation around it (null while it isn't shown).
    private IComposerHost? Host() => Above<IComposerHost>(this);

    private static T? Above<T>(DependencyObject from) where T : class
    {
        for (DependencyObject? d = VisualTreeHelper.GetParent(from); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T found) return found;
        return null;
    }

    private void Back() => Host()?.FocusComposer();

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // the inner scroller would keep the wheel even with scrolling off: hand it to the conversation
        e.Handled = true;
        if (VisualTreeHelper.GetParent(this) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = parent });
    }

    // Windows' own Cut / Copy / Paste menu would come up otherwise
    protected override void OnContextMenuOpening(ContextMenuEventArgs e) => e.Handled = true;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Selection.IsEmpty) Back(); // a click that selected nothing: back to typing
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        menu ??= new PopupMenu();
        menu.Open(new[]
        {
            new ActionItem { Title = Tr("복사", "Copy"), Keys = "Ctrl+C", Glyph = G.Copy, Run = CopyNow },
            new ActionItem { Title = Tr("모두 선택", "Select all"), Keys = "Ctrl+A", Glyph = G.Page, Run = SelectAllNow },
        });
    }

    private void SelectAllNow()
    {
        if (Above<ISelectionOwner>(this) is { } owner)
        {
            owner.SelectEverything();
            return;
        }
        Focus();
        SelectAll();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsCopyKey(ctrl, key))
        {
            CopyNow();
            e.Handled = true;
            return;
        }
        bool keeps = (ctrl && key == Key.A)
            || (shift && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt;
        if (!keeps) Back(); // typing goes on in the composer (the key itself follows the focus there)
        base.OnPreviewKeyDown(e);
    }
}
