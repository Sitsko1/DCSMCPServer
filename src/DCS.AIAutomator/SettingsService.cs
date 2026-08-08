using System;
using System.IO;
using Windows.Storage;

namespace DCS.AIAutomator;

/// <summary>
/// Reads/writes the app's configurable settings via ApplicationData.LocalSettings (packaged-app
/// key/value store, tied to this MSIX package's identity). Lives only in this project — never in
/// DcsMcpBridge, which the integration tests run unpackaged and ApplicationData throws there.
/// </summary>
public sealed class SettingsService
{
    private readonly ApplicationDataContainer _values = ApplicationData.Current.LocalSettings;

    public int McpPort
    {
        get => GetInt(nameof(McpPort), 5270);
        set => _values.Values[nameof(McpPort)] = value;
    }

    public string DcsHost
    {
        get => GetString(nameof(DcsHost), "127.0.0.1");
        set => _values.Values[nameof(DcsHost)] = value;
    }

    public int DcsPort
    {
        get => GetInt(nameof(DcsPort), 1024);
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

    public string McpListenUrl => $"http://127.0.0.1:{McpPort}";

    private static string DefaultSavedGamesPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "DCS");

    private string GetString(string key, string fallback) =>
        _values.Values.TryGetValue(key, out object? v) && v is string s ? s : fallback;

    private int GetInt(string key, int fallback) =>
        _values.Values.TryGetValue(key, out object? v) && v is int i ? i : fallback;
}
