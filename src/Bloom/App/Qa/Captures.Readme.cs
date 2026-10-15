using System;
using System.Threading.Tasks;
using Bloom.Core;
using Bloom.Ui;

namespace Bloom.App;

internal static partial class Captures
{
    /// `--qa-set readme` (dark, 2×; run it with --demo so nothing of this PC shows): the search line answering a few
    /// kinds of typing — 초성, a word typed with the keyboard left in Korean, an emoji, a reminder, a unit — for the
    /// README's search board, 창 배치 for its "more" board, 홈 편집 in a wide launcher (R-home-edit) and the color palette
    /// of a drawn poster (R-palette). The other README pictures come from the chatwin,
    /// notices, mine and motion sets and --qa-space, each run with --qa-scale 2.
    private static async Task ReadmeScreens(Host host, Launcher w, string dir)
    {
        Shots.Scale = 2;
        Theme.Apply("dark");
        Settings.Current.FilesInRoot = false;
        Settings.Current.WindowsInRoot = false;
        host.Apps.KeepForQa(Host.WindowsOwn); // also without --demo: the browsers and Windows' own tools only
        await Shot(w, dir, "R-chosung", Mode.Root, Tr("ㅁㅁㅈ", "ntpd"), 900);
        await Shot(w, dir, "R-typo", Mode.Root, Tr("초개ㅡㄷ", "chrom"), 700);
        await Shot(w, dir, "R-emoji", Mode.Root, Tr(":하트", ":heart"), 700);
        await Shot(w, dir, "R-reminder", Mode.Root, Tr("30분 뒤 빨래", "laundry in 30 min"), 700);
        await Shot(w, dir, "R-units", Mode.Root, Tr("30평 to m2", "5.9 ft to cm"), 700);
        // the README's "more" board: 창 배치 (no window of this PC is named in recording mode)
        await Shot(w, dir, "R-layout", Mode.Layout, "", 700);
        await ReadmeHomeScreens(host, w, dir);
        await ReadmePaletteScreens(host, w, dir);
        Shots.Scale = 1;
    }

    /// The README's home-editing board: a wide launcher (two columns) in 홈 편집, with each column's "+ 블록 추가".
    private static async Task ReadmeHomeScreens(Host host, Launcher w, string dir)
    {
        Settings.Current.Home = null;
        Timers.Add("timer", Tr("집중", "Focus"), DateTime.Now.AddMinutes(25));
        Timers.Add("reminder", Tr("회의", "Meeting"), DateTime.Now.AddHours(2));
        for (int i = 0; i < 60 && !host.Apps.Ready; i++) await Task.Delay(100);
        double width = w.Width, height = w.Height;
        w.Width = 1100;
        w.Height = 700;
        w.Go(Mode.Root, "");
        await Task.Delay(700);
        w.ToggleHomeEdit();
        await Task.Delay(500);
        await Snap(w, dir, "R-home-edit");
        w.ToggleHomeEdit();
        await Task.Delay(300);
        w.Width = width;
        w.Height = height;
        Settings.Current.Home = null;
    }

    /// The README's palette: Bloom Lens 7 on a drawn poster (no screen), a swatch picked so its gradients show.
    private static async Task ReadmePaletteScreens(Host host, Launcher w, string dir)
    {
        var poster = Poster(dir);
        host.SetLensForQa(new Ocr.ScreenRect(-32000, -32000, poster.w, poster.h), poster.px);
        await host.LensDo("palette");
        await Task.Delay(400);
        host.PickSwatch(2, false);
        await Snap(w, dir, "R-palette");
    }
}
