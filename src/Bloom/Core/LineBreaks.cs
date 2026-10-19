using System;
using System.Collections.Generic;
using System.Globalization;

namespace Bloom.Core;

/// Where the Markdown view (Ui\MdView.cs) may end a line, and the lines a text makes at a width — without the joiners
/// FlowDocument needed (Hangul.KeepAll): a text is shaped once on one line (its characters' x), and its lines are found
/// here by arithmetic. A line may end after spaces; Korean words keep whole (only spaces part them, CSS keep-all);
/// Chinese and Japanese break between characters but not before closing marks or after opening ones (basic kinsoku); a
/// hyphen or dash between letters may end one; code and addresses break where MdParser.CodeBreakAfter says; a word
/// longer than the line breaks between its grapheme clusters (an emoji sequence or a letter with its marks stays whole).
internal static class LineBreaks
{
    /// A picture in the text (drawn, not shaped): a line may break on either side of it.
    public const char ObjectChar = '\uFFFC';

    /// Inline code's padding in the text (drawn room, not shaped): it holds on to the code as a letter would.
    public const char PadChar = '\u2007';

    // no line starts with these (closing marks, small kana, the long vowel mark, iteration marks) …
    private const string NoStart = ")]}>,.:;!?%、。，．：；？！）］｝〉》」』】〕〗〙〛’”ゝゞヽヾーァィゥェォッャュョヮヵヶぁぃぅぇぉっゃゅょゎゕゖㇰㇱㇲㇳㇴㇵㇶㇷㇸㇹㇺㇻㇼㇽㇾㇿ々〻‐゠–〜～・…‥";

    // … and none ends with these (opening marks)
    private const string NoEnd = "([{<（［｛〈《「『【〔〖〘〚‘“";

    /// Space that a line may end after (and that hangs past the line's end): not a no-break space.
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\u3000' or '\u2002' or '\u2003' or '\u2009';

    /// Chinese characters, kana and CJK marks: a line may break between any two (not Hangul: keep-all).
    public static bool IsCjk(char c) =>
        (c >= '\u2E80' && c <= '\u2FFF') || (c >= '\u3000' && c <= '\u303F' && c != '\u3000') || (c >= '\u3040' && c <= '\u30FF') || (c >= '\u31F0' && c <= '\u31FF')
        || (c >= '\u3400' && c <= '\u4DBF') || (c >= '\u4E00' && c <= '\u9FFF') || (c >= '\uF900' && c <= '\uFAFF') || (c >= '\uFF00' && c <= '\uFF60') || (c >= '\uFF61' && c <= '\uFF9F');

    private static bool IsDash(char c) => c is '-' or '\u2010' or '\u2013' or '\u2014';

    /// The places a line may end in `text` (k: before text[k]; 0 &lt; k &lt; length), in order. '\n' (a hard break) and
    /// what follows a segment end are not among them: the caller ends lines there. `code`: ranges [start, end) drawn as
    /// code or an address, which also break by MdParser.CodeBreakAfter.
    public static int[] Opportunities(string text, IReadOnlyList<(int Start, int End)>? code = null)
    {
        int n = text.Length;
        var list = new List<int>(n / 4 + 1);
        if (n < 2) return [];
        var boundary = Graphemes(text);
        int codeAt = 0;
        for (int k = 1; k < n; k++)
        {
            if (!boundary[k]) continue;
            char prev = text[k - 1], next = text[k];
            if (prev == '\n' || next == '\n') continue;
            while (code is not null && codeAt < code.Count && code[codeAt].End <= k - 1) codeAt++;
            bool inCode = code is not null && codeAt < code.Count && code[codeAt].Start <= k - 1 && k < code[codeAt].End;
            if (IsSpace(next)) continue; // after the whole run of spaces
            if (IsSpace(prev))
            {
                list.Add(k);
                continue;
            }
            if (inCode && MdParser.CodeBreakAfter(text.AsSpan(code![codeAt].Start, code[codeAt].End - code[codeAt].Start), k - 1 - code[codeAt].Start))
            {
                list.Add(k);
                continue;
            }
            if (NoStart.Contains(next) || NoEnd.Contains(prev)) continue;
            if (IsCjk(prev) || IsCjk(next) || prev == ObjectChar || next == ObjectChar)
            {
                list.Add(k);
                continue;
            }
            // a hyphen or dash between letters or digits (well-known, re-read)
            if (IsDash(prev) && k >= 2 && char.IsLetterOrDigit(text[k - 2]) && char.IsLetterOrDigit(next)) list.Add(k);
        }
        return [.. list];
    }

    /// Whether each place k (0..length) is between two grapheme clusters.
    public static bool[] Graphemes(string text)
    {
        var b = new bool[text.Length + 1];
        b[0] = b[text.Length] = true;
        for (int i = 0; i < text.Length;)
        {
            int len = StringInfo.GetNextTextElementLength(text, i);
            if (len <= 0) len = 1;
            i += len;
            b[Math.Min(i, text.Length)] = true;
        }
        return b;
    }

    /// The lines `text` makes from `from` to `to` (a segment: no '\n' in it) at `width`, as the index each starts at:
    /// greedy — each line takes as much as fits, ending at the last opportunity whose text (spaces at its end hang)
    /// fits; a line with none takes the grapheme clusters that fit (at least one). `x`: each character's left edge on
    /// one unbroken line (x[to] its right end), `opportunities` sorted. Half a pixel of tolerance.
    public static void Break(string text, float[] x, int[] opportunities, int from, int to, double width, List<int> starts, ref bool[]? graphemes)
    {
        starts.Add(from);
        int s = from;
        while (true)
        {
            double limit = x[s] + width + 0.5;
            if (x[VisibleEnd(text, s, to)] <= limit) return;
            // the last opportunity in (s, to) whose visible end fits
            int lo = Array.BinarySearch(opportunities, s + 1);
            if (lo < 0) lo = ~lo;
            int hi = Array.BinarySearch(opportunities, to);
            if (hi < 0) hi = ~hi;
            int best = -1;
            int a = lo, z = hi - 1;
            while (a <= z)
            {
                int mid = (a + z) >> 1;
                if (x[VisibleEnd(text, s, opportunities[mid])] <= limit)
                {
                    best = mid;
                    a = mid + 1;
                }
                else z = mid - 1;
            }
            int next;
            if (best >= 0) next = opportunities[best];
            else
            {
                // too long a word: as many clusters as fit
                graphemes ??= Graphemes(text);
                next = -1;
                for (int k = s + 1; k < to && x[k] <= limit; k++)
                    if (graphemes[k]) next = k;
                if (next < 0)
                {
                    next = s + 1;
                    while (next < to && !graphemes[next]) next++;
                }
                if (next >= to) return;
            }
            starts.Add(next);
            s = next;
        }
    }

    /// Where the text from `start` to `end` stops being seen: spaces at its end hang past it.
    public static int VisibleEnd(string text, int start, int end)
    {
        while (end > start && IsSpace(text[end - 1])) end--;
        return end;
    }
}
