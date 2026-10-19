using System;
using System.Linq;
using System.Xml.Linq;
using Bloom.WinKey;

namespace Bloom.Tests;

/// The elevated Win-key helper's pure parts (issue #3): what it accepts as Bloom, the task it registers and who may
/// touch it, and the tap rule both hooks share, with the resync on the other hook's replayed release.
internal static class WinKeyHelperTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    public static void PidOnlyPlainDigits()
    {
        Check.Eq(WinKeyProtocol.ParsePid("1234"), 1234, "a process id");
        Check.Eq(WinKeyProtocol.ParsePid("2147483647"), int.MaxValue, "the largest id");
        foreach (string bad in new[] { "", "0", "-5", "+5", " 5", "5 ", "12a", "0x10", "2147483648", "99999999999", "１２", "٣", "5\0" })
            Check.Eq(WinKeyProtocol.ParsePid(bad), null, $"«{bad}» refused");
    }

    public static void OnlyBloomExe()
    {
        Check.True(WinKeyProtocol.IsBloomImage(@"C:\Users\a\AppData\Local\BloomLauncher\current\Bloom.exe"), "the installed Bloom");
        Check.True(WinKeyProtocol.IsBloomImage(@"D:\x\bloom.EXE"), "any case");
        Check.True(!WinKeyProtocol.IsBloomImage(@"C:\x\NotBloom.exe"), "another exe");
        Check.True(!WinKeyProtocol.IsBloomImage(@"C:\x\Bloom.exe.bak"), "another extension");
        Check.True(!WinKeyProtocol.IsBloomImage(@"C:\Bloom.exe\cmd.exe"), "a folder named Bloom.exe");
        Check.True(!WinKeyProtocol.IsBloomImage(null), "no image");
    }

    public static void TaskAccessAdminsChangeUserRuns()
    {
        Check.Eq(WinKeyProtocol.TaskSddl(Sid), $"O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;{Sid})", "task SDDL");
        Check.Eq(WinKeyProtocol.FolderSddl, "O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;AU)", "folder SDDL");
        Check.Eq(WinKeyProtocol.TaskName(Sid), "WinKeyHelper-" + Sid, "task name");
        foreach (string bad in new[] { "S-1-5)(A;;FA;;;WD", "S-1-", "", "s-1-5-21", "S-1-5-21-1 ", "S-1-5-21<x>" })
        {
            bool threw = false;
            try
            {
                _ = WinKeyProtocol.TaskSddl(bad);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Check.True(threw, $"«{bad}» is no SID");
        }
    }

    public static void TaskXml()
    {
        string exe = @"C:\Program Files\Bloom & Co\WinKeyHelper\WinKeyHelper.exe", dir = @"C:\Program Files\Bloom & Co\WinKeyHelper";
        var doc = XDocument.Parse(WinKeyProtocol.TaskXml(Sid, exe, dir));
        XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string V(params string[] path) => path.Aggregate(doc.Root!, (e, n) => e.Element(t + n)!).Value;
        Check.Eq(V("Principals", "Principal", "UserId"), Sid, "user");
        Check.Eq(V("Principals", "Principal", "LogonType"), "InteractiveToken", "logon");
        Check.Eq(V("Principals", "Principal", "RunLevel"), "HighestAvailable", "run level");
        Check.Eq(V("Settings", "ExecutionTimeLimit"), "PT0S", "no time limit");
        Check.Eq(V("Settings", "DisallowStartIfOnBatteries"), "false", "starts on battery");
        Check.Eq(V("Settings", "StopIfGoingOnBatteries"), "false", "keeps running on battery");
        Check.Eq(V("Settings", "Priority"), "4", "priority");
        Check.Eq(V("Settings", "MultipleInstancesPolicy"), "Parallel", "instances");
        Check.Eq(V("Settings", "IdleSettings", "StopOnIdleEnd"), "false", "idle end");
        Check.True(doc.Root!.Element(t + "Triggers") is null, "no trigger");
        Check.Eq(V("Actions", "Exec", "Command"), "\"" + exe + "\"", "the installed copy, quoted, absolute");
        Check.Eq(V("Actions", "Exec", "Arguments"), "$(Arg0)", "Bloom's pid");
        Check.Eq(V("Actions", "Exec", "WorkingDirectory"), dir, "its folder");
    }

    public static void ProtocolInNames()
    {
        Check.Eq(WinKeyProtocol.FireEvent, @"Local\Bloom.WinKey.v1.Fire", "fire");
        Check.Eq(WinKeyProtocol.StopEvent, @"Local\Bloom.WinKey.v1.Stop", "stop");
        Check.True(Version.Parse(WinKeyProtocol.FileVersion).Major.ToString() == WinKeyProtocol.Version, "file major = protocol");
    }

    private static bool Press(WinTap tap, uint vk, bool down, bool own = false, bool mods = false) => tap.Key(vk, down, !down, own, () => mods);

    public static void TapLoneWin()
    {
        var tap = new WinTap();
        Check.True(!Press(tap, WinTap.VK_LWIN, true), "down passes");
        Check.True(Press(tap, WinTap.VK_LWIN, false), "a lone press is a tap");
        Check.Eq(tap.WinDown, 0u, "released");
        Press(tap, WinTap.VK_RWIN, true);
        Check.True(Press(tap, WinTap.VK_RWIN, false), "the right Win too");
    }

    public static void TapCombos()
    {
        var tap = new WinTap();
        Press(tap, WinTap.VK_LWIN, true);
        Press(tap, 0x45 /* E */, true);
        Press(tap, 0x45, false);
        Check.True(!Press(tap, WinTap.VK_LWIN, false), "Win+E is no tap");
        Press(tap, WinTap.VK_LWIN, true, mods: true);
        Check.True(!Press(tap, WinTap.VK_LWIN, false), "Shift held first");
        Press(tap, WinTap.VK_LWIN, true);
        Press(tap, WinTap.VK_RWIN, true);
        Check.True(!Press(tap, WinTap.VK_LWIN, false), "both Win keys");
        Press(tap, WinTap.VK_RWIN, false);
        Press(tap, WinTap.VK_LWIN, true);
        Press(tap, WinTap.VK_LWIN, true); // auto-repeat
        Check.True(Press(tap, WinTap.VK_LWIN, false), "a held lone Win still taps");
    }

    /// The other hook took the real release and replayed it tagged: this one forgets the press, doesn't tap, and the
    /// next lone press taps again (never stuck, never a second summon).
    public static void TapResyncsOnReplayedRelease()
    {
        var tap = new WinTap();
        Press(tap, WinTap.VK_LWIN, true);
        Check.True(!Press(tap, WinTap.VK_MASK, true, own: true), "the replayed mask key passes");
        Press(tap, WinTap.VK_MASK, false, own: true);
        Check.True(!Press(tap, WinTap.VK_LWIN, false, own: true), "the replayed release is no tap");
        Check.Eq(tap.WinDown, 0u, "the press forgotten");
        Press(tap, 0x45, true); // typing right after: no Win held, nothing to mark
        Press(tap, 0x45, false);
        Press(tap, WinTap.VK_LWIN, true);
        Check.True(Press(tap, WinTap.VK_LWIN, false), "the next lone press taps");
    }

    /// A tagged key while Win is held is the other hook's mask key, not a combination.
    public static void TapIgnoresTaggedKeys()
    {
        var tap = new WinTap();
        Press(tap, WinTap.VK_LWIN, true);
        Press(tap, WinTap.VK_MASK, true, own: true);
        Press(tap, WinTap.VK_MASK, false, own: true);
        Check.True(Press(tap, WinTap.VK_LWIN, false), "still a tap");
        Press(tap, WinTap.VK_LWIN, true, own: true);
        Check.Eq(tap.WinDown, 0u, "a tagged Win press is not a press");
    }
}
