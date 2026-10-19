using System;
using System.IO;
using System.Linq;
using Bloom.Search;

namespace Bloom.Tests;

/// 내 명령: the placeholders, a chain's steps and waits, and how a script line is started.
internal static class CommandTests
{
    public static void Placeholders()
    {
        var now = new DateTime(2026, 10, 8, 14, 5, 0);
        Check.Eq(UserCommands.Expand("백업 {input} {date} {time}", "  D:\\사진 ", null, now), "백업 D:\\사진 2026-10-08 14:05", "input trimmed, date, time");
        Check.Eq(UserCommands.Expand("붙여: {CLIPBOARD}", "", () => "복사한 글", now), "붙여: 복사한 글", "clipboard, any case");
        Check.Eq(UserCommands.Expand("{clipboard}", "", null, now), "{clipboard}", "no clipboard reader: left as it is");
        Check.Eq(UserCommands.SnippetText("안녕하세요\\n감사합니다"), "안녕하세요\n감사합니다", "\\n is a new line in a snippet");
    }

    public static void ChainSteps()
    {
        Check.Eq(string.Join("|", UserCommands.Steps(" 메모장 ; 볼륨 30;;기다리기 2초 \n yt lofi ")), "메모장|볼륨 30|기다리기 2초|yt lofi", "split by ; and new lines, trimmed");
        Check.Eq(UserCommands.Wait("기다리기 2초"), TimeSpan.FromSeconds(2), "기다리기 2초");
        Check.Eq(UserCommands.Wait("wait 1.5"), TimeSpan.FromSeconds(1.5), "wait 1.5");
        Check.Eq(UserCommands.Wait("대기 600"), TimeSpan.FromSeconds(60), "at most a minute");
        Check.Eq(UserCommands.Wait("기다리기"), null, "no number: not a wait");
        Check.Eq(UserCommands.Wait("볼륨 30"), null, "another step");
    }

    public static void ScriptStart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bloom-tests", "my scripts");
        Directory.CreateDirectory(dir);
        string ps1 = Path.Combine(dir, "백업.ps1"), bat = Path.Combine(dir, "run.bat");
        File.WriteAllText(ps1, "Write-Output ok");
        File.WriteAllText(bat, "@echo ok");

        Check.Eq(UserCommands.SplitFile($"{ps1} -Path D:\\a"), (ps1, "-Path D:\\a"), "an unquoted path with spaces, then its arguments");
        Check.Eq(UserCommands.SplitFile($"\"{bat}\" x"), (bat, "x"), "a quoted path");
        Check.Eq(UserCommands.SplitFile("Get-Date -Format yyyy").file, null, "a command line has no file");

        var hidden = UserCommands.StartInfo($"{ps1} -Path D:\\a", false);
        Check.True(hidden.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase), ".ps1 through PowerShell");
        Check.True(hidden.ArgumentList[^1].Contains("UTF8") && hidden.ArgumentList[^1].EndsWith($"& '{ps1}' -Path D:\\a", StringComparison.Ordinal) && !hidden.ArgumentList.Contains("-NoExit"), "hidden: UTF-8 output, the file and its arguments");
        Check.True(hidden.CreateNoWindow && hidden.RedirectStandardOutput && !hidden.UseShellExecute, "hidden: no window, output read");
        Check.Eq(hidden.WorkingDirectory, dir, "runs in the script's folder");

        var shown = UserCommands.StartInfo($"\"{bat}\"", true);
        Check.True(shown.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && shown.Arguments.StartsWith("/k ", StringComparison.Ordinal) && shown.UseShellExecute, ".bat in a console that stays open");

        var line = UserCommands.StartInfo("Get-ChildItem \"C:\\\"", false);
        Check.True(line.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) && line.ArgumentList[^1].EndsWith("Get-ChildItem \"C:\\\"", StringComparison.Ordinal), "a command line as one PowerShell argument, as typed (ArgumentList escapes it)");
        Check.Eq(UserCommands.LastLine("one\r\ntwo\r\n\r\n"), "two", "the last line with words");

        Check.Eq(UserCommands.ScriptLine($"{ps1} {{input}}", "D:\\내 사진"), $"{ps1} 'D:\\내 사진'", "{input} with a space: one PowerShell argument");
        Check.Eq(UserCommands.ScriptLine($"\"{bat}\" {{input}}", "a b"), $"\"{bat}\" \"a b\"", "for a .bat: double quotes");
        Check.Eq(UserCommands.ScriptLine("Write-Output \"{input}\"", "a b"), "Write-Output \"a b\"", "already quoted in the line: as typed");
        Check.Eq(UserCommands.ScriptLine("Write-Output {input}", "abc"), "Write-Output 'abc'", "a bare {input}: always a quoted string");
    }

    private static readonly string Marker = Path.Combine(Path.GetTempPath(), "bloom-tests", "injected-" + Guid.NewGuid().ToString("N") + ".txt");

    private static string NoCr(string s) => s.Replace("\r\n", "\n").TrimEnd();

    /// Runs a template through the real PowerShell path (StartInfo, hidden) and returns what it printed.
    private static string RunPs(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        using var p = System.Diagnostics.Process.Start(UserCommands.StartInfo(line, false))!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60000)) throw new InvalidOperationException("powershell did not end");
        if (error.Trim().Length > 0) throw new InvalidOperationException("powershell said: " + error.Trim() + " ← for: " + line);
        return NoCr(output);
    }

    /// F1: whatever the value holds, it stays data in a bare, '…', "…", mid-word and $( '…' ) spot — checked with a real
    /// PowerShell: the output is the value, and the command the value tried to run (New-Item <marker>) never ran.
    public static void InjectionStaysData()
    {
        string m = Marker;
        string[] values =
        {
            $"x';New-Item '{m}';'",
            $"\"$(New-Item '{m}')\"",
            $"a b\u2019;New-Item '{m}';#",
            $"a b\u2018;New-Item '{m}';\u2019",
            $"line one\nline two';New-Item '{m}';'\nend",
            $"a\u00A0b\u2003c';New-Item '{m}'",
            $"`$(New-Item '{m}') $env:USERNAME `\"",
            $"a\"; New-Item '{m}'; \"",
            $"a\u201D; New-Item '{m}'; \u201C",
            "-Force",
        };
        string sep = "Write-Output '=='";
        string[] shapes = { "Write-Output {input}", "Write-Output '{input}'", "Write-Output \"{input}\"", "Write-Output a{input}b", "Write-Output \"pre $('{input}') post\"" };
        foreach (string v in values)
        {
            string value = v.Trim();
            string body = string.Join("; " + sep + "; ", shapes);
            string expected = string.Join("\n==\n", new[] { value, value, value, "a" + value + "b", "pre " + value + " post" }.Select(NoCr));
            string got = RunPs(UserCommands.ScriptLine(body, v));
            Check.Eq(got, expected, "input stays data: " + v.Replace("\n", "⏎"));
            Check.True(!File.Exists(m), "nothing ran for: " + v.Replace("\n", "⏎"));
        }

        // {clipboard} goes through the same path (not trimmed: what was copied is what comes)
        string clip = $"x';New-Item '{m}';'\n\"$(New-Item '{m}')\"\u00A0\u2019";
        string gotClip = RunPs(UserCommands.ScriptLine(string.Join("; " + sep + "; ", shapes.Select(s => s.Replace("{input}", "{clipboard}"))), "", () => clip));
        Check.Eq(gotClip, string.Join("\n==\n", new[] { clip, clip, clip, "a" + clip + "b", "pre " + clip + " post" }.Select(NoCr)), "clipboard stays data");
        Check.True(!File.Exists(m), "nothing ran for the clipboard");

        // after a comment the value can't start a new line; inside a here-string it can't end the string
        string evil = $"x\nWrite-Output 'PWNED'; New-Item '{m}'\n#";
        Check.Eq(RunPs(UserCommands.ScriptLine("Write-Output 'a' # {input}\nWrite-Output 'b'", evil)), "a\nb", "a comment ends only at its own line end");
        string hereValue = $"x\n'@\nNew-Item '{m}'\n@'\ny";
        Check.Eq(RunPs(UserCommands.ScriptLine("Write-Output @'\n{input}\n'@", hereValue)), NoCr(hereValue.Replace("\n", " ")), "a here-string ends only at its own '@");
        Check.True(!File.Exists(m), "nothing ran after comment / here-string");
        Check.Eq(RunPs(UserCommands.ScriptLine("Write-Output \"[$(Write-Output '{input}')]\"", $"a';New-Item '{m}';'b")), "[a';New-Item '" + m + "';'b]", "inside $( ) of a double-quoted string");
        Check.True(!File.Exists(m), "nothing ran inside $( )");
    }

    /// F1: a program or .bat gets the value inside "…" only — no quote, line break or (cmd) % to break out, no escape of
    /// the closing quote by a trailing backslash; and winget's Id and Source are quoted unless plain.
    public static void ProgramAndWingetQuoting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bloom-tests", "my scripts");
        Directory.CreateDirectory(dir);
        string bat = Path.Combine(dir, "run.bat");
        File.WriteAllText(bat, "@echo ok");
        Check.Eq(UserCommands.ScriptLine($"\"{bat}\" {{input}}", "a\" & calc %PATH%\nx"), $"\"{bat}\" \"a & calc PATH x\"", "a .bat: one quoted argument, no quote, break or %");
        Check.Eq(UserCommands.ScriptLine($"\"{bat}\" \"{{input}}\"", "a & b"), $"\"{bat}\" \"a & b\"", "a .bat whose template quotes it: no second pair");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Check.Eq(UserCommands.ScriptLine($"{cmd} {{input}}", @"D:\x\"), $"{cmd} \"D:\\x\\\\\"", "a program: a trailing backslash doubled");
        Check.Eq(UserCommands.ScriptLine($"{cmd} {{input}}", ""), $"{cmd} ", "nothing typed: nothing added");

        var plain = new WingetPackage("Notepad++", "Notepad++.Notepad++", "1", "winget");
        Check.True(Winget.CommandLine(plain).StartsWith("winget install --id Notepad++.Notepad++ --exact --source winget ", StringComparison.Ordinal), "a plain Id stays as it is");
        string evil = Winget.CommandLine(new WingetPackage("x", "a';calc;'", "1", "m s;calc"));
        Check.True(evil.Contains("--id 'a'';calc;'''") && evil.Contains("--source 'm s;calc'"), "an odd Id or Source is a '…' string: " + evil);
        Check.True(Winget.CommandLine(new WingetPackage("x", "--source", "1", "winget")).Contains("--id '--source'"), "an Id that looks like an option is quoted");
    }
}
