using System;
using System.Globalization;
using System.IO;
using System.Security;

namespace Bloom.WinKey;

/// What Bloom and WinKeyHelper.exe agree on (issue #3: the Win key over apps run as administrator). Compiled into
/// both, so the names, the tag, the task and its access rules have one owner.
internal static class WinKeyProtocol
{
    /// Moves only when the two sides stop understanding each other. It is in every object name and is the helper
    /// file's major version, so Bloom can tell an installed copy of another protocol (and offer to install it again).
    public const string Version = "1";
    public const string FileVersion = Version + ".0.0.0";

    /// Bloom (medium integrity) creates both; the helper only opens them. Fire: a lone Win press was taken over
    /// an elevated window. Stop: the helper leaves.
    public const string FireEvent = @"Local\Bloom.WinKey.v" + Version + ".Fire";
    public const string StopEvent = @"Local\Bloom.WinKey.v" + Version + ".Stop";

    /// Marks the keys a hook injects ("ASLW"): the other hook lets them through and resyncs on them.
    public static readonly UIntPtr OwnTag = (UIntPtr)0x41534C57u;

    public const string ExeName = "WinKeyHelper.exe";
    public const string BloomExe = "Bloom.exe";
    public const string TaskFolder = @"\Bloom";
    public const string TaskPrefix = "WinKeyHelper-";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BloomWinKeyHelper";
    public const string DisplayName = "Bloom Win-key helper";

    /// The installed copy's folder: under Program Files it inherits the admin-only access.
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bloom", "WinKeyHelper");

    public static string TaskName(string sid) => TaskPrefix + Sid(sid);

    /// The helper's one argument: Bloom's process id, plain ASCII digits only (no sign, spaces or other digits).
    public static int? ParsePid(string arg)
    {
        if (arg.Length is 0 or > 10) return null;
        foreach (char c in arg)
            if (c is < '0' or > '9') return null;
        return int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : null;
    }

    /// The process the helper serves runs Bloom.exe.
    public static bool IsBloomImage(string? path) => path is not null && string.Equals(Path.GetFileName(path), BloomExe, StringComparison.OrdinalIgnoreCase);

    /// Administrators and SYSTEM may change the task; its user may only read and run it. Owned by Administrators:
    /// an owner may always rewrite the access rules, so it is never the user.
    public static string TaskSddl(string sid) => $"O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;{Sid(sid)})";

    /// The \Bloom folder: every signed-in user may find their task in it, none may add to it.
    public const string FolderSddl = "O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;AU)";

    /// The task: no trigger (Bloom runs it, its process id as $(Arg0)), the user's own sign-in, the full admin
    /// token, no time limit, not held back on battery, normal priority, one copy per run asked for.
    public static string TaskXml(string sid, string exe, string dir) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>Bloom</Author>
            <Description>Bloom: the Win key over apps run as administrator</Description>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{Sid(sid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{SecurityElement.Escape(exe)}"</Command>
              <Arguments>$(Arg0)</Arguments>
              <WorkingDirectory>{SecurityElement.Escape(dir)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    /// A string SID (S-1-…, digits and dashes): nothing else reaches the task's name, XML or access rules.
    private static string Sid(string sid)
    {
        bool ok = sid.Length > 4 && sid.StartsWith("S-1-", StringComparison.Ordinal);
        foreach (char c in sid.AsSpan(Math.Min(4, sid.Length)))
            ok &= c is '-' or (>= '0' and <= '9');
        return ok ? sid : throw new ArgumentException("not a SID", nameof(sid));
    }
}
