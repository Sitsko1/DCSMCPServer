using System.IO.Compression;
using System.Text;
using DCS.AIAutomator.Core;

namespace DCS.AIAutomator.Mcp;

/// <summary>
/// Builds the "Export logs…" zip for bug reports (#23): the app's and the Claude Desktop relay's
/// log files plus an <c>about.txt</c>. Only files named like <see cref="DcsLogging"/> writes them
/// are taken, so nothing else that may sit in the folder (e.g. config backups) can leak into it.
/// Logs never contain secrets, tool arguments or command bodies, and about.txt is built only from
/// values that aren't secret either (it takes no keys at all).
/// </summary>
public static class DiagnosticsExport
{
    public const string AboutFileName = "about.txt";

    /// <summary>Writes the zip to <paramref name="destination"/> (left open).</summary>
    public static void WriteZip(Stream destination, string logDirectory, string about)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (string file in LogFiles(logDirectory))
        {
            ZipArchiveEntry entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
            // Today's file is held open by the running logger: read it with sharing allowed.
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
        using var writer = new StreamWriter(zip.CreateEntry(AboutFileName).Open(), new UTF8Encoding(false));
        writer.Write(about);
    }

    /// <summary>The app's and the relay's log files (both start with <see cref="DcsLogging.FileNamePrefix"/>), oldest first.</summary>
    public static IReadOnlyList<string> LogFiles(string logDirectory) =>
        Directory.Exists(logDirectory)
            ? Directory.EnumerateFiles(logDirectory, DcsLogging.FileNamePrefix + "*.clef").Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>Plain-text facts that help diagnose a report. Every input is non-secret.</summary>
    /// <param name="agents">Each known AI agent and whether it's installed on this PC.</param>
    public static string About(string appVersion, string windowsVersion, BridgeStatus status,
        IEnumerable<(string Name, bool Detected)> agents, DateTimeOffset nowUtc)
    {
        McpClientSnapshot clients = status.McpClients.Snapshot(nowUtc);
        MissionInfo? mission = status.CurrentMission;
        string dcs =
            status.DcsAuthFailed ? "AUTH FAILED"
            : status.DcsScriptOutdated ? "SCRIPT OUTDATED"
            : !status.DcsConnected ? "DISCONNECTED"
            : status.DcsNotResponding ? "NOT RESPONDING"
            : status.DcsPaused ? "PAUSED"
            : "CONNECTED";

        var text = new StringBuilder();
        text.AppendLine("DCS.AIAutomator diagnostics");
        text.AppendLine($"Exported:       {nowUtc:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine($"App version:    {appVersion}");
        text.AppendLine($"Windows:        {windowsVersion}");
        text.AppendLine($"Wire protocol:  {LuaHooksScriptGenerator.ProtocolVersion}");
        text.AppendLine();
        text.AppendLine($"Bridge:         {status.BridgeState} at {status.McpEndpoint}");
        text.AppendLine($"DCS:            {dcs} ({status.DcsEndpoint})");
        text.AppendLine($"Mission:        {(mission is null ? "none" : $"{mission.MissionName} | {mission.Terrain} | {mission.Aircraft}")}");
        text.AppendLine($"AI clients:     {clients.State}{(clients.ClientNames.Count > 0 ? $" ({string.Join(", ", clients.ClientNames)})" : "")}");
        text.AppendLine($"Units:          {status.Units}");
        text.AppendLine();
        text.AppendLine("AI agents on this PC:");
        foreach (var (name, detected) in agents)
        {
            text.AppendLine($"  {name}: {(detected ? "detected" : "not found")}");
        }
        return text.ToString();
    }
}
