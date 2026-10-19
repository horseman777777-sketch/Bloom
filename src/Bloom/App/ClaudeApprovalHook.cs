using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using Bloom.Core;
using Bloom.Native;

namespace Bloom.App;

/// `Bloom.exe --claude-approve [--data <dir>]`: Claude Code's PermissionRequest hook (Ai\ClaudeHooks). Carries the
/// request and the Claude Code process that asks to the running Bloom (ClaudeApprovals) over a pipe only this Windows
/// user can open, then prints the answer as the hook's JSON. No Bloom running, or an answer of "the terminal", prints
/// nothing: Claude Code then asks in the terminal as it would without the hook. No window, no WPF.
internal static class ClaudeApprovalHook
{
    public const string Arg = "--claude-approve";

    /// Bloom's approval pipe.
    public static string Pipe => Paths.Pipe("approve");

    private static readonly UTF8Encoding Utf8 = new(false);

    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--data");
        Paths.Init(at >= 0 && at + 1 < args.Length ? args[at + 1] : null);
        string input;
        using (var reader = new StreamReader(Console.OpenStandardInput(), Utf8)) input = reader.ReadToEnd();
        int parent = Win32.ProcessTree().TryGetValue(Environment.ProcessId, out var me) ? me.parent : 0; // the Claude Code that asks
        if (Output(Ask(Pipe, parent, input)) is { } json)
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(Utf8.GetBytes(json));
        }
        return 0;
    }

    /// The running Bloom's answer: "allow", "deny", or null (no Bloom, or the terminal asks).
    internal static string? Ask(string pipe, int pid, string input)
    {
        if (!Directory.EnumerateFiles(@"\\.\pipe\", pipe).Any()) return null; // Bloom isn't running: no wait for it
        try
        {
            using var link = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            link.Connect(1000);
            using var writer = new StreamWriter(link, Utf8, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new { pid, input }));
            using var reader = new StreamReader(link, Utf8, leaveOpen: true); // the link closes last
            string? answer = reader.ReadLine();
            return answer is "allow" or "deny" ? answer : null;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The hook's JSON for an answer (null: no decision).
    internal static string? Output(string? answer) => answer switch
    {
        "allow" => JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = "allow" } } }),
        "deny" => JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = "deny", message = "The user declined this on Bloom's notice card." } } }),
        _ => null,
    };
}
