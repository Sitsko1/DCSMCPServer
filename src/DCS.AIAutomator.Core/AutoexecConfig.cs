using System.IO;
using System.Text.RegularExpressions;

namespace DCS.AIAutomator.Core;

/// <summary>
/// The two <c>Saved Games\DCS\Config\autoexec.cfg</c> settings DCS requires before a Hooks script
/// may run code in the mission scripting environment (<c>net.dostring_in("mission", ...)</c>, see
/// DCS World/API/Sim_ControlAPI.md). Only ever written with the user's consent. Appends to
/// whatever lists the file already builds instead of replacing them, so other tools' entries survive.
/// </summary>
public static class AutoexecConfig
{
    public const string FileName = "autoexec.cfg";

    private const string Marker = "-- DCS.AIAutomator:";

    private static readonly string Block =
        $$"""

        {{Marker}} lets its Hooks script show messages and task AI in missions (Settings > DCS Integration).
        net.allow_unsafe_api = net.allow_unsafe_api or {}
        net.allow_unsafe_api[#net.allow_unsafe_api + 1] = "userhooks"
        net.allow_dostring_in = net.allow_dostring_in or {}
        net.allow_dostring_in[#net.allow_dostring_in + 1] = "mission"

        """;

    public static string PathFor(string savedGamesPath) => Path.Combine(savedGamesPath, "Config", FileName);

    /// <summary>
    /// True if our block is there, or the user set both up themselves (one line each naming
    /// "userhooks" and "mission"). Doesn't evaluate the Lua, so an odd hand-written file may read wrong.
    /// </summary>
    public static bool IsEnabled(string savedGamesPath)
    {
        string path = PathFor(savedGamesPath);
        if (!File.Exists(path)) return false;
        string text = File.ReadAllText(path);
        return text.Contains(Marker)
            || (Regex.IsMatch(text, @"allow_unsafe_api[^\n]*""userhooks""") && Regex.IsMatch(text, @"allow_dostring_in[^\n]*""mission"""));
    }

    /// <summary>Appends our block (keeping a .bak of an existing file). No-op when already enabled.</summary>
    public static void Enable(string savedGamesPath)
    {
        if (IsEnabled(savedGamesPath)) return;
        string path = PathFor(savedGamesPath);
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.AppendAllText(path, Block);
    }
}
