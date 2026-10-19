using System;
using System.Text;

namespace Bloom.Core;

/// Hangul helpers for search: 초성 strings ("메모장" → "ㅁㅁㅈ"), and the 2-beolsik key string
/// ("메모장" → "apahwkd", "초개ㅡㄷ" → "chrome"), which makes a query typed in the wrong input mode,
/// or still mid-composition ("멤" while typing 메모), match the same way as the intended text.
internal static class Hangul
{
    private const string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";

    private static readonly string[] ChoKeys = { "r", "R", "s", "e", "E", "f", "a", "q", "Q", "t", "T", "d", "w", "W", "c", "z", "x", "v", "g" };

    private static readonly string[] JungKeys =
    {
        "k", "o", "i", "O", "j", "p", "u", "P", "h", "hk", "ho", "hl", "y", "n", "nj", "np", "nl", "b", "m", "ml", "l",
    };

    private static readonly string[] JongKeys =
    {
        "", "r", "R", "rt", "s", "sw", "sg", "e", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg", "a", "q", "qt", "t", "T", "d", "w", "c", "z", "x", "v", "g",
    };

    // compatibility jamo (U+3131…U+3163) → keys
    private static readonly string[] CompatKeys =
    {
        "r", "R", "rt", "s", "sw", "sg", "e", "E", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg", "a", "q", "Q", "qt", "t", "T", "d", "w", "W", "c", "z", "x", "v", "g", // ㄱ…ㅎ
        "k", "o", "i", "O", "j", "p", "u", "P", "h", "hk", "ho", "hl", "y", "n", "nj", "np", "nl", "b", "m", "ml", "l", // ㅏ…ㅣ
    };

    public static bool IsSyllable(char c) => c >= '가' && c <= '힣';

    public static bool IsCompatJamo(char c) => c >= 'ㄱ' && c <= 'ㅣ';

    public static bool IsCompatConsonant(char c) => c >= 'ㄱ' && c <= 'ㅎ';

    public static bool HasHangul(string s)
    {
        foreach (char c in s)
            if (IsSyllable(c) || IsCompatJamo(c))
                return true;
        return false;
    }

    /// True when every non-space char is a consonant jamo (a 초성 query such as "ㅋㅋㅇ").
    public static bool IsChosungQuery(string s)
    {
        bool any = false;
        foreach (char c in s)
        {
            if (c == ' ') continue;
            if (!IsCompatConsonant(c)) return false;
            any = true;
        }
        return any;
    }

    public static string Chosung(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (IsSyllable(c)) sb.Append(Cho[(c - 0xAC00) / 588]);
            else if (c != ' ') sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// Lower-cased 2-beolsik key string; spaces dropped.
    public static string Keys(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char c in s)
        {
            if (IsSyllable(c))
            {
                int i = c - 0xAC00;
                sb.Append(ChoKeys[i / 588]).Append(JungKeys[i % 588 / 28]).Append(JongKeys[i % 28]);
            }
            else if (IsCompatJamo(c)) sb.Append(CompatKeys[c - 0x3131]);
            else if (c != ' ') sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }

    private const char Joiner = '\u2060'; // WORD JOINER

    /// Korean words keep together when wrapped (CSS keep-all): a WORD JOINER between syllables.
    /// Display only — copied text comes from the original string, or goes through Unjoin (text selected on screen).
    public static string KeepAll(string s)
    {
        if (s.Length < 2) return s;
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && IsSyllable(c) && IsSyllable(s[i - 1]))
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 16);
                sb.Append(Joiner);
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? s;
    }

    /// What Ui\Markdown.cs draws inline code with: its padding, a space no line break comes off (FIGURE SPACE), and after
    /// . / \ _ - in it a chance to break a long one there (ZERO WIDTH SPACE). Display only, as KeepAll's joiners.
    public const char CodePad = '\u2007', BreakHint = '\u200B';

    /// The display-only characters undone: shown text back to what was written (a selection copied from the screen) —
    /// KeepAll's joiners and code's break chances out, code's padding back to the spaces it was.
    public static string Unjoin(string s)
    {
        if (s.AsSpan().IndexOfAny(Joiner, BreakHint, CodePad) < 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (c is not Joiner and not BreakHint) sb.Append(c == CodePad ? ' ' : c);
        return sb.ToString();
    }
}
