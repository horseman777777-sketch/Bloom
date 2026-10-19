using System.IO;
using System.Threading.Tasks;
using Bloom.Core;
using Bloom.Ui;

namespace Bloom.App;

internal static partial class Captures
{
    private const string MarkdownNote = "# 장보기\n이번 주 **토요일** 전에 사 둘 것 · [가게 지도](https://example.com)\n\n## 할 일\n- [ ] 우유 2개\n- [x] 계란 한 판\n- [ ] 식빵\n\n## 순서\n1. 할인 쿠폰 챙기기\n2. 장바구니 가져가기\n   - 큰 것 하나\n\n> 영수증은 `가계부` 폴더에\n\n| 품목 | 가격 |\n|---|---|\n| 우유 | 2,800원 |\n| 계란 | 6,900원 |\n\n```js\nconst total = 2800 + 6900;\n```\n";

    private const string ChatAnswer = "세 어셈블리가 한 방향으로 참조해요. 설정은 `Contoso.WeatherService.Editor.Shell.PageHidden`에 저장되고 `Editor/Shell/MainWindow.cs`가 읽어요.\n\n| 어셈블리 | 폴더 | 하는 일 |\n|---|---|---|\n| `Contoso.WeatherService.Runtime` | `Runtime/` | 장면 컴포넌트와 셰이더 |\n| `Contoso.WeatherService.Core` | `Editor/Core/` | 그리지 않는 로직: 설정, 경로, `L10n`, 서비스 |\n| `Contoso.WeatherService.UI` | `Editor/UI/` | UI 키트 |";

    /// `--qa-set notesmd`: 메모 as Markdown beside the launcher, dark and light: the list (a "# 장보기" note titled
    /// 장보기), the note being written, its 보기 (headings, lists, a checklist with one box ticked; scrolled down: a quote,
    /// a table, code), 보기 at text size 18 with its "글자 크기 18", the editor at 18, and a chat answer with a table of
    /// long code (m6-chat; notesmd.txt: the title and sizes).
    private static async Task NotesMdScreens(Host host, Launcher w, string dir)
    {
        var log = new System.Text.StringBuilder();
        var note = Notes.New(MarkdownNote);
        log.AppendLine("title: " + note.Title);
        w.SetPinned(true);
        foreach (string theme in new[] { "dark", "light" })
        {
            Theme.Apply(theme);
            w.Go(Mode.Root);
            host.Tools.Open("notes");
            var notes = (NotesTool)host.Tools.Find("notes")!.Tool;
            await Group(host, w, dir, $"{theme}-m1-list");
            notes.Show(note.Id);
            await Group(host, w, dir, $"{theme}-m2-edit");
            var pane = notes.PaneForQa!;
            pane.InView = true;
            await Group(host, w, dir, $"{theme}-m3-view");
            pane.ScrollToEndForQa(); // the quote, the table and the code
            await Group(host, w, dir, $"{theme}-m3b-view-end");
            pane.SetTextSize(18);
            await Group(host, w, dir, $"{theme}-m4-view-18");
            log.AppendLine($"{theme}: size {MdPane.TextSize} (setting {Settings.Current.MdTextSize})");
            pane.InView = false;
            await Group(host, w, dir, $"{theme}-m5-edit-18");
            pane.SetTextSize(Settings.MdTextDefault);
            host.Tools.Close(host.Tools.Find("notes")!);
            // a chat answer with a table of long code and code in a wrapping sentence (a local example, never sent)
            var chat = new Bloom.Ai.ChatThread { Id = "capture-md", Title = "코드 위치", Model = "sonnet" };
            chat.Messages.Add(new Bloom.Ai.ChatMessage { Role = "user", Text = "어셈블리별로 정리해줘" });
            chat.Messages.Add(new Bloom.Ai.ChatMessage { Role = "assistant", Text = ChatAnswer });
            w.ShowChat(chat);
            await Snap(w, dir, $"{theme}-m6-chat");
        }
        w.SetPinned(false);
        File.WriteAllText(Path.Combine(dir, "notesmd.txt"), log.ToString());
    }
}
