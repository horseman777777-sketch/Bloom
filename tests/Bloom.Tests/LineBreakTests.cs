using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Bloom.Core;
using Bloom.Ui;

namespace Bloom.Tests;

/// Where the Markdown view ends lines (Core\LineBreaks.cs) and the lines a shaped text makes (Ui\MdText.cs): Korean kept
/// whole, Chinese and Japanese between characters with kinsoku, code and addresses after . / \ _ -, emoji sequences and
/// letters with marks never split, styles changing inside a word, tabs, no-break spaces, hard breaks.
internal static class LineBreakTests
{
    private static MdEnv Env() => new(14, true, 1.0);

    private static MdText Shaped(MdEnv env, params MdSpan[] spans)
    {
        var t = MdText.Of([.. spans], env, 14, FontWeights.Normal, Theme.P.Text, null);
        t.Shape(env);
        return t;
    }

    private static MdSpan T(string s, MdStyle style = MdStyle.None) => new(MdSpanKind.Text, s, style);

    public static void HangulBreaksOnlyAtSpaces()
    {
        const string s = "한국어 문장이 여러 줄로 이어집니다";
        var opps = LineBreaks.Opportunities(s);
        var spaces = Enumerable.Range(1, s.Length - 1).Where(k => s[k - 1] == ' ').ToArray();
        Check.Eq(string.Join(",", opps), string.Join(",", spaces), "only after spaces");
        var env = Env();
        var t = Shaped(env, T(s));
        foreach (double width in new[] { 80.0, 100, 120, 200 }) // the longest word (5 syllables) fits each
            foreach (int start in t.Break(width).Skip(1))
                Check.True(s[start - 1] == ' ' || start == 0, $"at {width} px a line starts at a word: {start}");
    }

    public static void CjkBreaksBetweenCharactersWithKinsoku()
    {
        const string s = "日本語のテキストです。（括弧）とカッコ";
        var opps = LineBreaks.Opportunities(s).ToHashSet();
        Check.True(opps.Contains(1) && opps.Contains(2), "between ideographs");
        Check.True(!opps.Contains(s.IndexOf('。')), "not before 。");
        Check.True(opps.Contains(s.IndexOf('。') + 1), "after 。, before an opening mark");
        Check.True(!opps.Contains(s.IndexOf('（') + 1), "not after （");
        Check.True(!opps.Contains(s.IndexOf('）')), "not before ）");
        Check.True(!opps.Contains(s.IndexOf('ッ')), "not before a small kana");
        Check.True(LineBreaks.Opportunities("ラーメン").SequenceEqual([2, 3]), "not before the long vowel mark");
    }

    public static void CodeAndAddressesBreakAfterMarks()
    {
        var env = Env();
        var code = Shaped(env, T("see "), T("Contoso.WeatherService.Runtime", MdStyle.Code));
        string text = code.Text;
        Check.True(text[4] == LineBreaks.PadChar && text[^1] == LineBreaks.PadChar, "code with its padding as one character each side");
        var inCode = code.Opportunities.Where(k => k > 5).ToArray();
        Check.Eq(string.Join(",", inCode.Select(k => text[k - 1])), ".,.", "in code: after the dots only");
        Check.True(!code.Opportunities.Contains(5) && code.Opportunities.Contains(4), "the padding holds on to the code; the space before it breaks");
        var lines = code.Break(120);
        Check.True(lines.Length > 1 && lines.Skip(1).All(k => text[k - 1] is '.' or ' '), "a narrow line breaks code after its dots");

        string url = "https://example.com/" + new string('a', 20) + "/" + new string('b', 30) + "/c";
        var link = Shaped(env, new MdSpan(MdSpanKind.Text, MdParser.Autolink(url), MdStyle.None, new MdLink(url), url));
        Check.True(link.Opportunities.Any(k => link.Text[k - 1] == '/'), "an address breaks after a slash");
        Check.Eq(link.Copy(0, link.Text.Length), url, "and copies whole");
    }

    public static void ClustersStayWhole()
    {
        var env = Env();
        const string family = "👩‍👩‍👧‍👦";
        var emoji = Shaped(env, T(family + family + family));
        var starts = emoji.Break(5);
        Check.Eq(string.Join(",", starts), $"0,{family.Length},{2 * family.Length}", "an emoji sequence is never split");
        var marks = Shaped(env, T("e\u0301e\u0301e\u0301e\u0301"));
        Check.True(marks.Break(3).All(k => k % 2 == 0), "a letter keeps its combining mark");
        Check.True(LineBreaks.Opportunities("a\u0301 b").SequenceEqual([3]), "and a break after a space only");
    }

    public static void StyleChangeInsideAWord()
    {
        var env = Env();
        var t = Shaped(env, T("bo", MdStyle.Bold), T("ld words"));
        Check.True(!t.Opportunities.Contains(2), "no break where the style changes");
        Check.True(t.X[2] > t.X[0] && t.X[3] > t.X[2], "x goes on over the change");
        for (int i = 0; i < t.Text.Length; i++) Check.True(t.X[i + 1] >= t.X[i], "x never goes back");
    }

    public static void TabsAndNoBreakSpaces()
    {
        var env = Env();
        var tab = Shaped(env, T("a\tb"));
        double space = env.Measure(" ", tab.LookAt(0));
        Check.Near(tab.X[2] - tab.X[1], 4 * space, 0.01, "a tab: four spaces");
        Check.True(tab.Opportunities.Contains(2), "a line may end after a tab");
        Check.True(LineBreaks.Opportunities("a\u00A0b c").SequenceEqual([4]), "not at a no-break space, after a space");
    }

    public static void HardBreaksEndLines()
    {
        var env = Env();
        var t = Shaped(env, T("one"), MdSpan.Break, T("two"));
        Check.Eq(string.Join(",", t.Break(1000)), "0,4", "a hard break starts a line");
        Check.Eq(t.Copy(0, t.Text.Length), "one\r\ntwo", "copied as a line break");
        Check.Eq(t.X[4], 0f, "the next segment starts at 0");
    }

    public static void LinesFitAndMatchTheirShaping()
    {
        var env = Env();
        var t = Shaped(env, T("Bloom의 보기 화면은 표가 많은 문서도 멈추지 않고 그립니다. Each line is broken by arithmetic over the shaped positions, "), T("Contoso.Weather", MdStyle.Code), T(" 그리고 日本語の文も"), T(" 섞여 있습니다.", MdStyle.Bold));
        foreach (double width in new[] { 60.0, 90, 140, 220, 330, 500 })
        {
            var starts = t.Break(width);
            for (int k = 0; k < starts.Length; k++)
            {
                int s = starts[k], e = t.LineEnd(starts, k);
                double visible = t.X[LineBreaks.VisibleEnd(t.Text, s, e)] - t.X[s];
                bool forced = !t.Opportunities.Any(o => o > s && o < e) && visible > width;
                Check.True(visible <= width + 0.5 || forced, $"at {width}: line {k} is {visible:0.0} wide");
                double alone = t.FormattedWidth(env, s, e), shaped = t.X[e] - t.X[s];
                Check.True(Math.Abs(alone - shaped) <= 0.5, $"at {width}: line {k} formatted alone {alone:0.00} vs shaped {shaped:0.00}");
            }
        }
    }
}
