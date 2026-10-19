using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Bloom.Core;

namespace Bloom.Ai;

/// A Codex sign-in folder's auth.json, as the light way (ChatGptLight) reads and refreshes it. A refresh spends the old
/// refresh token, so the tokens it gets back are the only good ones: they go into auth.json through a Bloom-only file
/// (auth.bloom.<tag>.tmp, never one of Codex's), and when that fails (Codex or a scanner holds auth.json, the disk says
/// no) they wait — in memory, and in that file for a Bloom started again. The pending file is also written while a login
/// terminal is open, so its waiting tokens survive a Bloom restart; replacing auth.json waits for the terminal to close.
/// They replace auth.json only while it still holds the refresh token they were made from (the tag): a sign-in since
/// (UsageLogin, `codex login`) wins. Every read, every app-server fallback and every Codex start in
/// an account folder settles them first. Left open: a `codex login` run by hand into a Bloom folder in the instant
/// between Store's check and its move.
internal static class CodexAuth
{
    public sealed record Auth(JsonObject Raw, string Access, string? Refresh, string? IdToken, string? AccountId);

    /// Refreshed tokens not in auth.json yet, and the refresh token auth.json held when they were made.
    private sealed record Waiting(Auth Auth, string Base);

    private static readonly Dictionary<string, Waiting> waiting = new(StringComparer.OrdinalIgnoreCase); // by folder; locked

    private static readonly HashSet<string> refreshing = new(StringComparer.OrdinalIgnoreCase); // a token request out; locked by waiting

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string DefaultHome() => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(Paths.Home, ".codex");

    /// The folder's sign-in, waiting tokens first; null when there is none.
    public static Auth? Read(string home) => Settle(home) ?? Load(Path.Combine(home, "auth.json"));

    /// Tries to put waiting tokens in place once more; true when they still wait, a refresh is out, or a pending file is
    /// still on disk (auth.json could not even be read) — Codex in this folder would read spent tokens. Null = the default folder.
    public static bool Unsaved(string? home)
    {
        home ??= DefaultHome();
        if (Settle(home) is not null) return true;
        lock (waiting)
            if (refreshing.Contains(home)) return true;
        return Directory.Exists(home) && Directory.EnumerateFiles(home, "auth.bloom.*.tmp").Any();
    }

    /// A token request for the folder is out (ChatGptLight.Refresh, until its answer is saved or refused).
    public static void Refreshing(string home, bool on)
    {
        lock (waiting)
        {
            if (on) refreshing.Add(home);
            else refreshing.Remove(home);
        }
    }

    /// Tokens a refresh of `from` (what Read gave) got back: kept until they are in auth.json, tried a few times now.
    public static async Task Save(string home, Auth from, Auth fresh)
    {
        lock (waiting)
            waiting[home] = new Waiting(fresh, waiting.TryGetValue(home, out var w) && ReferenceEquals(w.Auth, from) ? w.Base : from.Refresh!);
        for (int i = 0; Settle(home) is not null; i++)
        {
            if (i == 4)
            {
                Log.Write("ai usage: chatgpt (light) could not save the refreshed sign-in yet; it waits for the next look");
                return;
            }
            await Task.Delay(200);
        }
    }

    /// The folder's account signed out and leaves (UsageLogin.SignOut): its waiting tokens go, in memory and on disk, so
    /// none reaches the Recycle Bin with the folder.
    public static void Forget(string home)
    {
        lock (waiting)
        {
            waiting.Remove(home);
            if (!Directory.Exists(home)) return;
            foreach (string f in Directory.GetFiles(home, "auth.bloom.*.tmp")) Drop(f);
        }
    }

    public static Auth? From(JsonObject raw) => raw["tokens"] is JsonObject t && Str(t, "access_token") is { } access
        ? new Auth(raw, access, Str(t, "refresh_token"), Str(t, "id_token"), Str(t, "account_id"))
        : null;

    /// The file waiting tokens made from `refreshToken` are saved through (a short hash: the token itself stays out of names).
    public static string PendingName(string refreshToken) =>
        "auth.bloom." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)))[..16].ToLowerInvariant() + ".tmp";

    /// The waiting tokens after one more try at saving them; null when none wait.
    private static Auth? Settle(string home)
    {
        lock (waiting)
        {
            if (!waiting.ContainsKey(home)) Restore(home);
            if (!waiting.TryGetValue(home, out var w)) return null;
            Store(home, w);
            return waiting.TryGetValue(home, out w) ? w.Auth : null;
        }
    }

    /// A Bloom started again: a pending file whose tag is auth.json's refresh token waits again; any other is spent
    /// (signed in anew since) or cut short.
    private static void Restore(string home)
    {
        if (!Directory.Exists(home)) return;
        string[] files = Directory.GetFiles(home, "auth.bloom.*.tmp");
        if (files.Length == 0) return;
        try
        {
            string? now = Load(Path.Combine(home, "auth.json"))?.Refresh;
            foreach (string f in files)
            {
                if (now is not null && Path.GetFileName(f) == PendingName(now) && Load(f) is { } a) waiting[home] = new Waiting(a, now);
                else Drop(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            /* held: the next look tries again */
        }
    }

    /// One try at putting waiting tokens into auth.json, only while it still holds the refresh token they were made from.
    /// The pending file is written while a login terminal is open too, so a Bloom restart can restore it; only the move waits.
    private static void Store(string home, Waiting w)
    {
        string file = Path.Combine(home, "auth.json"), pending = Path.Combine(home, PendingName(w.Base));
        try
        {
            if (Load(file)?.Refresh != w.Base)
            {
                waiting.Remove(home); // signed in anew since, or signed out: these are not wanted any more
                Drop(pending);
                return;
            }
            File.WriteAllText(pending, w.Auth.Raw.ToJsonString(Indented));
            // a login terminal is open on this folder: the CLI writes auth.json there and UsageLogin.Run takes a newer file for
            // a finished sign-in, so the login's own file comes first; the pending file survives a Bloom restart (dropped if it lands)
            if (LoginMarker.Alive(home)) return;
            File.Move(pending, file, true);
            waiting.Remove(home);
            _ = CodexAppHome.Changed(home); // app-servers on the file it replaced end before the new one is linked
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            /* still waiting: the next look tries again */
        }
    }

    /// Null when the file is missing or not a sign-in; a held file throws (the caller tries again later).
    private static Auth? Load(string file)
    {
        if (!File.Exists(file)) return null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            return null;
        }
        return node is JsonObject raw ? From(raw) : null;
    }

    private static void Drop(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            /* tried again on the next look */
        }
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
