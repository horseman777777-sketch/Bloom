using Bloom.Core;

namespace Bloom.Tests;

/// A checklist box ticked in the source (Core\MarkdownText.cs): only that one character changes, line breaks and all
/// else kept byte for byte, and only while the line is still the one that was drawn.
internal static class MarkdownTextTests
{
    public static void TogglesInPlace()
    {
        string lf = "# 할 일\n- [ ] 우유\n- [x] 계란\n";
        Check.Eq(MarkdownText.ToggleTask(lf, 1, "- [ ] 우유"), "# 할 일\n- [x] 우유\n- [x] 계란\n", "LF: the box ticks, the rest as it was");
        Check.Eq(MarkdownText.ToggleTask(lf, 2, "- [x] 계란"), "# 할 일\n- [ ] 우유\n- [ ] 계란\n", "a ticked box clears");

        string crlf = "a\r\n- [ ] b\r\n  * [X] c\r\nend";
        Check.Eq(MarkdownText.ToggleTask(crlf, 1, "- [ ] b"), "a\r\n- [x] b\r\n  * [X] c\r\nend", "CRLF kept");
        Check.Eq(MarkdownText.ToggleTask(crlf, 2, "  * [X] c"), "a\r\n- [ ] b\r\n  * [ ] c\r\nend", "an X clears, the indent kept");
        Check.Eq(MarkdownText.Line(crlf, 1), "- [ ] b", "a line comes without its \\r");
        Check.Eq(MarkdownText.Line(crlf, 3), "end", "the last line has no break");
        Check.True(MarkdownText.Line(crlf, 4) is null, "past the end: no line");

        string mixed = "x\n+ [ ] y\r\nz\n";
        Check.Eq(MarkdownText.ToggleTask(mixed, 1, "+ [ ] y"), "x\n+ [x] y\r\nz\n", "mixed line breaks each kept");
        Check.Eq(MarkdownText.ToggleTask("- [ ] only", 0, "- [ ] only"), "- [x] only", "a text without any break");
    }

    public static void NumberedAndQuoted()
    {
        string text = "1. [ ] one\n12) [x] two\n> - [ ] quoted\n>> 3. [X] deeper";
        Check.Eq(MarkdownText.ToggleTask(text, 0, "1. [ ] one"), "1. [x] one\n12) [x] two\n> - [ ] quoted\n>> 3. [X] deeper", "1. item");
        Check.Eq(MarkdownText.ToggleTask(text, 1, "12) [x] two"), "1. [ ] one\n12) [ ] two\n> - [ ] quoted\n>> 3. [X] deeper", "12) item");
        Check.Eq(MarkdownText.ToggleTask(text, 2, "> - [ ] quoted"), "1. [ ] one\n12) [x] two\n> - [x] quoted\n>> 3. [X] deeper", "in a quote");
        Check.Eq(MarkdownText.ToggleTask(text, 3, ">> 3. [X] deeper"), "1. [ ] one\n12) [x] two\n> - [ ] quoted\n>> 3. [ ] deeper", "in a quote in a quote");
    }

    public static void RefusesWhatChanged()
    {
        string text = "# 장보기\n- [ ] 우유\n- 계란\n";
        Check.True(MarkdownText.ToggleTask(text, 1, "- [ ] 두유") is null, "the line is not the one drawn any more");
        Check.True(MarkdownText.ToggleTask(text, 2, "- 계란") is null, "a plain list item has no box");
        Check.True(MarkdownText.ToggleTask(text, 0, "# 장보기") is null, "a heading has no box");
        Check.True(MarkdownText.ToggleTask(text, 9, "- [ ] 우유") is null && MarkdownText.ToggleTask(text, -1, "- [ ] 우유") is null, "no such line");
        Check.True(MarkdownText.ToggleTask("-[ ] tight", 0, "-[ ] tight") is null, "no space after the marker: not a list item");
        Check.True(MarkdownText.ToggleTask("- [x]done", 0, "- [x]done") is null, "no space after the box: no checklist item (none is drawn)");
        Check.Eq(MarkdownText.ToggleTask("- [ ]", 0, "- [ ]"), "- [x]", "a box with nothing after it is one");
        Check.Eq(MarkdownText.ToggleTask("123456789. [ ] far", 0, "123456789. [ ] far"), "123456789. [x] far", "up to 9 digits");
        Check.True(MarkdownText.ToggleTask("1234567890. [ ] too far", 0, "1234567890. [ ] too far") is null, "10 digits: not a list item");
    }

    /// The renderer's rule is this one: each line it draws a box for, ToggleTask ticks; none other.
    public static void SameRuleAsDrawn()
    {
        foreach (string line in new[] { "- [ ] a", "  * [x] b", "1234. [X] c", "- [ ]", "- [x]done", "-[ ] d", "+  [ ]  e", "1) [ ]\tf", "- [-] g" })
        {
            var item = MarkdownText.ListItem.Match(line);
            bool drawn = item.Success && MarkdownText.TaskBox.IsMatch(item.Groups[3].Value);
            Check.Eq(MarkdownText.ToggleTask(line, 0, line) is not null, drawn, "drawn and ticked alike: " + line);
        }
    }

    public static void NotInCode()
    {
        string text = "```md\n- [ ] in code\n```\n- [ ] after\n~~~\n* [x] tilde code\n~~~";
        Check.True(MarkdownText.ToggleTask(text, 1, "- [ ] in code") is null, "a line in fenced code is no checklist item");
        Check.True(MarkdownText.ToggleTask(text, 5, "* [x] tilde code") is null, "nor in ~~~ code");
        Check.Eq(MarkdownText.ToggleTask(text, 3, "- [ ] after"), "```md\n- [ ] in code\n```\n- [x] after\n~~~\n* [x] tilde code\n~~~", "after the fence closes it is one again");
        Check.True(MarkdownText.Fence("  ```cs") == "```" && MarkdownText.Fence("~~~") == "~~~" && MarkdownText.Fence("- ```") is null, "fences");
        string inList = "- a\n  ```\n  - [ ] in code\n  ```\n- [ ] after";
        Check.True(MarkdownText.ToggleTask(inList, 2, "  - [ ] in code") is null, "a fence indented under a list item opens code too");
        Check.Eq(MarkdownText.ToggleTask(inList, 4, "- [ ] after"), "- a\n  ```\n  - [ ] in code\n  ```\n- [x] after", "and closes it");
    }
}
