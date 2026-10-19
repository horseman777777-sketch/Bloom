using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace Bloom.Ui;

/// The look tool windows (ToolWindow) and document windows (DocWindow) share: Bloom's glass, a 40-dip head that is the
/// window's caption (the handle to drag it by) with the window's glyph, name and buttons, a hairline under it, the
/// content below; the window sized by its edges.
internal static class GlassFrame
{
    public const double HeadHeight = 40;

    /// The window made ready for the glass: transparent, drawn sharp, its caption the head.
    public static void Prepare(Window w)
    {
        w.WindowStyle = WindowStyle.SingleBorderWindow;
        w.Background = Brushes.Transparent;
        w.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(w, new WindowChrome { CaptionHeight = HeadHeight, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
    }

    /// The head: the glyph and the name on the left (`name`, for its tooltip), `buttons` on the right.
    public static Grid Head(char glyph, string title, StackPanel buttons, out FrameworkElement name)
    {
        var head = new Grid { Margin = new Thickness(14, 0, 6, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var mark = Theme.Glyph(glyph, 13, Theme.P.Accent);
        mark.Margin = new Thickness(0, 0, 8, 0);
        stack.Children.Add(mark);
        stack.Children.Add(Theme.T(title, 13, Theme.P.Text, Theme.Semi));
        head.Children.Add(stack);
        buttons.Orientation = Orientation.Horizontal;
        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        name = stack;
        return head;
    }

    /// The window's whole surface: the head, its line, the content; drawn at Bloom's scale by the caller.
    public static SmoothBorder Frame(FrameworkElement head, FrameworkElement content)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeadHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(head);
        var line = new SmoothBorder { Background = Theme.P.Line };
        Grid.SetRow(line, 1);
        grid.Children.Add(line);
        Grid.SetRow(content, 2);
        grid.Children.Add(content);
        return new SmoothBorder { Child = grid };
    }

    /// A glyph button that still takes clicks inside the draggable head.
    public static SmoothBorder HeadButton(char glyph, string tip, Action click)
    {
        var b = ToolUi.Button(glyph, tip, click);
        b.Margin = new Thickness(2, 0, 0, 0);
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        return b;
    }

    /// The theme now: the glass (or the solid colour without transparency effects) and the frame's border.
    public static void Paint(Window w, SmoothBorder frame)
    {
        frame.Background = Theme.Acrylic ? Theme.P.Tint : Theme.P.Solid;
        if (new System.Windows.Interop.WindowInteropHelper(w).Handle != IntPtr.Zero)
            Glass.Apply(w, Theme.P.Dark, Theme.Acrylic, Theme.P.Dark ? Color.FromRgb(0x35, 0x2E, 0x31) : Color.FromRgb(0xD9, 0xD2, 0xD5));
    }
}
