using System.Diagnostics;
using System.IO;

namespace DCS.AIAutomator.Core;

/// <summary>
/// Starting, detecting and force-quitting the DCS process itself (#43), separate from the
/// app's DCS *connection* (which needs a mission and the Hooks script). Standalone installs only:
/// launches <c>bin-mt\DCS.exe</c> (multi-threaded, DCS's default) or the classic <c>bin\DCS.exe</c>.
/// A clean quit goes through the Hooks script (<see cref="DcsCommands.QuitAsync"/>); killing the
/// process is only the confirmed fallback.
/// </summary>
public static class DcsProcess
{
    /// <summary>Both builds' executable is DCS.exe.</summary>
    public const string ProcessName = "DCS";

    /// <summary>The executable to launch, or null with the reason it can't be found.</summary>
    public static (string? Path, string? Error) Executable(string installPath, bool multithreaded)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return (null, "Set the DCS install path in Settings → Paths first.");
        string exe = Path.Combine(installPath.Trim(), multithreaded ? "bin-mt" : "bin", "DCS.exe");
        return File.Exists(exe)
            ? (exe, null)
            : (null, $"{exe} wasn't found. Check the DCS install path, or the multi-threaded choice, in Settings → Paths.");
    }

    public static bool IsRunning()
    {
        Process[] processes = Process.GetProcessesByName(ProcessName);
        foreach (Process p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public static void Start(string executable) =>
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        })?.Dispose();

    /// <summary>Waits for every DCS process to exit; false if one is still running after <paramref name="timeout"/>.</summary>
    public static async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (IsRunning())
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(250, cancellationToken);
        }
        return true;
    }

    /// <summary>Force-kills DCS. Only after the user confirmed it; DCS skips its own shutdown.</summary>
    public static void Kill()
    {
        foreach (Process p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
            }
        }
    }
}
