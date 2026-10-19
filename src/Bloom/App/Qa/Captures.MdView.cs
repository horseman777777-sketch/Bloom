using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Bloom.Ui;

namespace Bloom.App;

internal static partial class Captures
{
    private const string ReadingSample = "# 읽기 시험\n\n첫 문단입니다. 여러 줄로 이어지는 문장이 한 문단으로 보이는지 봅니다.\n\n둘째 문단: 선택이 여기서 시작해서\n\n- 목록 하나를 지나\n- 목록 둘도 지나\n\n셋째 문단에서 끝납니다. 그 뒤는 선택되지 않아요.\n\n## 다음 구역\n\n마지막 문단.\n";

    /// `--qa-set mdview`: the document window's 보기 (Ui\MdView.cs) off-screen, dark and light: a selection running over
    /// several blocks (v1-select: the same colour in each box, the focused one or not; drawn with the window's adorner
    /// layer, where a text box draws its selection) and the window 1400 wide (v2-wide: the text at its reading width in
    /// the middle). mdview.txt: what each box on screen has selected, and the copy.
    private static async Task MdViewScreens(Host host, Launcher w, string dir)
    {
        string md = Path.Combine(dir, "mdview-src", "읽기 시험.md");
        Directory.CreateDirectory(Path.GetDirectoryName(md)!);
        File.WriteAllText(md, ReadingSample, new UTF8Encoding(false));
        var log = new StringBuilder();
        DocWindow.OffScreenForQa = true;
        foreach (string theme in new[] { "dark", "light" })
        {
            Theme.Apply(theme);
            var d = DocWindow.Show(md);
            d.Activate(); // as when someone drags in it
            await Task.Delay(300);
            if (d.PaneForQa.KeyTarget is MdView view)
            {
                System.Windows.Input.Keyboard.Focus(view);
                view.Select(2, 8, 5, 10); // from 둘째 문단 into 셋째 문단
                await Task.Delay(300);
                log.AppendLine($"{theme} boxes: {view.PaintedForQa()} · keyboard in the view {view.IsKeyboardFocused}");
                log.AppendLine($"{theme} copy: {view.SelectedText().Replace("\r\n", " ⏎ ", System.StringComparison.Ordinal)}");
            }
            await WithAdorners(d, dir, $"{theme}-v1-select");
            d.Width = 1400;
            await Doc(d, dir, $"{theme}-v2-wide");
            d.Close();
        }
        File.WriteAllText(Path.Combine(dir, "mdview.txt"), log.ToString());
    }

    /// A document window drawn with its adorner layer (where a text box draws a selection it isn't focused in).
    private static async Task WithAdorners(DocWindow d, string dir, string name)
    {
        d.FrameForQa.Background = Theme.P.Solid;
        await Task.Delay(450);
        await d.Dispatcher.InvokeAsync(() =>
        {
            var shot = AdornerLayer.GetAdornerLayer(d.FrameForQa) is { } layer && VisualTreeHelper.GetParent(layer) is FrameworkElement deco ? deco : d.FrameForQa;
            Shots.Save(shot, Path.Combine(dir, name + ".png"));
        }, DispatcherPriority.ApplicationIdle);
    }
}
