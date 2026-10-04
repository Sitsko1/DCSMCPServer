using System.IO;
using System.Text.RegularExpressions;

namespace DCS.AIAutomator.Core;

/// <summary>
/// The two <c>Saved Games\DCS\Config\autoexec.cfg</c> settings DCS requires before a Hooks script
/// may run code in the mission scripting environment (<c>net.dostring_in("scripting", ...)</c>, see
/// DCS World/API/Sim_ControlAPI.md). Only ever written with the user's consent. Appends to
/// whatever lists the file already builds instead of replacing them, so other tools' entries
/// survive (verified live: DCS honours the appended entries).
/// </summary>
public static class AutoexecConfig
{
    public const string FileName = "autoexec.cfg";

    private static readonly string Block =
        """

        -- DCS.AIAutomator: lets its Hooks script show messages and task AI in missions (Settings > DCS Integration).
        net.allow_unsafe_api = net.allow_unsafe_api or {}
        net.allow_unsafe_api[#net.allow_unsafe_api + 1] = "userhooks"
        net.allow_dostring_in = net.allow_dostring_in or {}
        net.allow_dostring_in[#net.allow_dostring_in + 1] = "scripting"

        """;

    public static string PathFor(string savedGamesPath) => Path.Combine(savedGamesPath, "Config", FileName);

    /// <summary>
    /// True if some line names "userhooks" for allow_unsafe_api and some line names "scripting" for
    /// allow_dostring_in — ours or the user's own. A block from an earlier build that allowed only
    /// "mission" reads as not enabled, so Deploy offers the current block. Doesn't evaluate the Lua,
    /// so an odd hand-written file may read wrong.
    /// </summary>
    public static bool IsEnabled(string savedGamesPath)
    {
        string path = PathFor(savedGamesPath);
        if (!File.Exists(path)) return false;
        string text = File.ReadAllText(path);
        return Regex.IsMatch(text, @"allow_unsafe_api[^\n]*""userhooks""")
            && Regex.IsMatch(text, @"allow_dostring_in[^\n]*""scripting""");
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
