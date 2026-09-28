using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace DCS.AIAutomator;

/// <summary>
/// Reads/writes the app's configurable settings via ApplicationData.LocalSettings (packaged-app
/// key/value store, tied to this MSIX package's identity). Lives only in this project — never in
/// DcsMcpBridge, which the integration tests run unpackaged and ApplicationData throws there.
/// </summary>
public sealed class SettingsService
{
    public const int DefaultMcpPort = 5270;
    public const string DefaultDcsHost = "127.0.0.1";
    public const int DefaultDcsPort = 1024;

    private readonly ApplicationDataContainer _values = ApplicationData.Current.LocalSettings;

    public int McpPort
    {
        get => GetInt(nameof(McpPort), DefaultMcpPort);
        set => _values.Values[nameof(McpPort)] = value;
    }

    public string DcsHost
    {
        get => GetString(nameof(DcsHost), DefaultDcsHost);
        set => _values.Values[nameof(DcsHost)] = value;
    }

    public int DcsPort
    {
        get => GetInt(nameof(DcsPort), DefaultDcsPort);
        set => _values.Values[nameof(DcsPort)] = value;
    }

    public string DcsInstallPath
    {
        get => GetString(nameof(DcsInstallPath), string.Empty);
        set => _values.Values[nameof(DcsInstallPath)] = value;
    }

    public string DcsSavedGamesPath
    {
        get => GetString(nameof(DcsSavedGamesPath), DefaultSavedGamesPath());
        set => _values.Values[nameof(DcsSavedGamesPath)] = value;
    }

    public int ToastDurationSeconds
    {
        get => GetInt(nameof(ToastDurationSeconds), 5);
        set => _values.Values[nameof(ToastDurationSeconds)] = value;
    }

    public string McpListenUrl => $"http://127.0.0.1:{McpPort}";

    // Ask Windows for the Saved Games known folder: users commonly relocate it (e.g. to another
    // drive), so %USERPROFILE%\Saved Games is only a fallback. Environment.SpecialFolder has no
    // SavedGames member, hence the P/Invoke.
    private static string DefaultSavedGamesPath()
    {
        var savedGamesFolderId = new Guid("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4"); // FOLDERID_SavedGames
        string root = SHGetKnownFolderPath(savedGamesFolderId, 0, IntPtr.Zero, out string? path) == 0 && path is not null
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        return Path.Combine(root, "DCS");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
        [MarshalAs(UnmanagedType.LPWStr)] out string? ppszPath);

    private string GetString(string key, string fallback) =>
        _values.Values.TryGetValue(key, out object? v) && v is string s ? s : fallback;

    private int GetInt(string key, int fallback) =>
        _values.Values.TryGetValue(key, out object? v) && v is int i ? i : fallback;
}
