using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DCS.AIAutomator.Agents;

/// <summary>
/// Runs the <c>claude mcp</c> commands built by <see cref="ClaudeCodeRegistration"/>: removes any
/// previous entry, then adds the current URL + key, so re-running after a key or port change
/// updates rather than duplicates. Arguments go through ArgumentList (no shell); the key is
/// redacted from anything shown or logged.
/// </summary>
public static class ClaudeCodeRegistrar
{
    public sealed record Result(bool Success, bool ClaudeNotFound, string Message);

    public static async Task<Result> RegisterAsync(string mcpUrl, string apiKey)
    {
        try
        {
            // Not found is fine (first registration); anything else will show up on the add.
            await RunClaudeAsync(ClaudeCodeRegistration.RemoveArguments());

            (int exitCode, string output) = await RunClaudeAsync(ClaudeCodeRegistration.AddArguments(mcpUrl, apiKey));
            string safeOutput = ClaudeCodeRegistration.Redact(output, apiKey).Trim();
            return exitCode == 0
                ? new Result(true, false, "Registered with Claude Code (user scope). Restart any open Claude Code sessions.")
                : new Result(false, false, $"claude exited with code {exitCode}: {safeOutput}");
        }
        catch (Win32Exception)
        {
            return new Result(false, true, "Claude Code (the 'claude' command) wasn't found. Install it, or copy the command and run it yourself.");
        }
    }

    /// <summary>True/false from <c>claude mcp get</c> (exit 0 = registered); null if claude can't be run.</summary>
    public static async Task<bool?> IsRegisteredAsync()
    {
        try
        {
            (int exitCode, _) = await RunClaudeAsync(ClaudeCodeRegistration.GetArguments());
            return exitCode == 0;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public static async Task<Result> UnregisterAsync()
    {
        try
        {
            (int exitCode, string output) = await RunClaudeAsync(ClaudeCodeRegistration.RemoveArguments());
            if (exitCode != 0 && ClaudeCodeRegistration.IsNotRegisteredOutput(output))
            {
                return new Result(true, false, "Already disconnected from Claude Code."); // nothing to remove
            }
            return exitCode == 0
                ? new Result(true, false, "Removed from Claude Code.")
                : new Result(false, false, $"claude exited with code {exitCode}: {output.Trim()}");
        }
        catch (Win32Exception)
        {
            return new Result(false, true, "Claude Code (the 'claude' command) wasn't found.");
        }
    }

    /// <summary>Whether a <c>claude</c> executable is on PATH (no process started).</summary>
    public static bool IsInstalled()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(dir.Trim(), name))) return true;
                }
                catch (ArgumentException)
                {
                    // malformed PATH entry
                }
            }
        }
        return false;
    }

    private static async Task<(int ExitCode, string Output)> RunClaudeAsync(string[] arguments)
    {
        var startInfo = new ProcessStartInfo("claude")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ?? throw new Win32Exception("claude did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out after 30 s");
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
