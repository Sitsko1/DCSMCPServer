using System.IO;

namespace DCS.Scripting;

/// <summary>
/// Sanity-checks the two user-entered DCS folders so they can't be swapped or mistyped. Each
/// method returns null when the path is valid, or a user-facing error message otherwise.
/// </summary>
public static class DcsPathValidator
{
    /// <summary>Optional: empty is valid. Otherwise must contain bin\DCS.exe or bin-mt\DCS.exe.</summary>
    public static string? ValidateInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Directory.Exists(path)) return "Folder does not exist.";
        return IsInstallFolder(path)
            ? null
            : "Not a DCS World install folder — expected bin\\DCS.exe or bin-mt\\DCS.exe inside it.";
    }

    /// <summary>
    /// Required: the DCS write folder (e.g. Saved Games\DCS), identified by its Config folder.
    /// The install folder also has a Config folder, so it's ruled out explicitly first.
    /// </summary>
    public static string? ValidateSavedGamesPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "A Saved Games folder is required.";
        if (!Directory.Exists(path)) return "Folder does not exist.";
        if (IsInstallFolder(path))
            return "This is the DCS install folder — use the DCS folder under Saved Games instead (e.g. Saved Games\\DCS).";
        return Directory.Exists(Path.Combine(path, "Config"))
            ? null
            : "Not a DCS Saved Games folder — expected a Config folder inside it. Has DCS been launched at least once?";
    }

    private static bool IsInstallFolder(string path) =>
        File.Exists(Path.Combine(path, "bin", "DCS.exe")) || File.Exists(Path.Combine(path, "bin-mt", "DCS.exe"));
}
