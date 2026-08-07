using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Spawns the real DcsMcpBridge server as a subprocess and drives it over stdio exactly like an
/// MCP client would, asserting on the raw JSON-RPC responses. This is the automated version of
/// the manual smoke test used during development — it would have caught both the circular-DI
/// hang and the AOT JsonSerializerContext gap that hand testing found.
/// </summary>
public class McpServerIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

    private Process _process = null!;
    private readonly StringBuilder _stderr = new();

    public Task InitializeAsync()
    {
        string exePath = FindServerExePath();
        Assert.True(File.Exists(exePath), $"Expected self-contained build of DcsMcpBridge at {exePath} " +
            "(build src/DcsMcpBridge first — the ProjectReference triggers this automatically for dotnet test).");

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start DcsMcpBridge process.");
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _stderr.AppendLine(e.Data); };
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    /// <summary>
    /// DcsMcpBridge.csproj is self-contained (SelfContained=true), so the ProjectReference copy in
    /// this project's own output only carries the thin framework-dependent files — no
    /// hostpolicy.dll/CLR — and fails to launch. The real self-contained payload lives in
    /// DcsMcpBridge's own bin/&lt;Config&gt;/net10.0/&lt;RID&gt;/ output, which we run directly.
    /// </summary>
    private static string FindServerExePath()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string rid = RuntimeInformation.RuntimeIdentifier;
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "DcsMcpBridge.exe" : "DcsMcpBridge";
        return Path.Combine(repoRoot, "src", "DcsMcpBridge", "bin", configuration, "net10.0", rid, exeName);
    }

    public Task DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        _process.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Initialize_ToolsList_ToolsCall_RoundTripCorrectly()
    {
        await SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"integration-test","version":"0.0.1"}}}""");
        await SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        await SendAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"send_atc_instruction","arguments":{"aircraft_callsign":"Enfield 1-1","action":"Vectors","heading":270}}}""");

        // The server may handle requests concurrently, so responses aren't guaranteed to arrive
        // in submission order — read 3 responses and match each by its "id" instead.
        Dictionary<int, JsonDocument> responsesById = [];
        for (int i = 0; i < 3; i++)
        {
            JsonDocument response = await ReadResponseAsync();
            responsesById[response.RootElement.GetProperty("id").GetInt32()] = response;
        }

        using (responsesById[1])
        {
            Assert.Equal("DcsMcpBridge", responsesById[1].RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        }

        using (responsesById[2])
        {
            JsonElement tools = responsesById[2].RootElement.GetProperty("result").GetProperty("tools");
            string?[] toolNames = [.. tools.EnumerateArray().Select(t => t.GetProperty("name").GetString())];
            Assert.Contains("send_atc_instruction", toolNames);
            Assert.Contains("get_random_number", toolNames);
        }

        using (responsesById[3])
        {
            string? resultText = responsesById[3].RootElement
                .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            // No real DCS instance is listening on 127.0.0.1:1024 in the test environment — this is
            // the correct, expected response, not a failure.
            Assert.Equal("Error: DCS interface is down.", resultText);
        }
    }

    private async Task SendAsync(string json)
    {
        try
        {
            await _process.StandardInput.WriteLineAsync(json);
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Failed writing to server stdin: {ex.Message}. HasExited={_process.HasExited} " +
                $"ExitCode={(_process.HasExited ? _process.ExitCode : "n/a")}\nStderr:\n{_stderr}");
        }
    }

    private async Task<JsonDocument> ReadResponseAsync()
    {
        string? line = await _process.StandardOutput.ReadLineAsync().WaitAsync(ResponseTimeout);
        if (string.IsNullOrEmpty(line))
        {
            Assert.Fail($"No JSON-RPC response line. HasExited={_process.HasExited} " +
                $"ExitCode={(_process.HasExited ? _process.ExitCode : "n/a")}\nStderr:\n{_stderr}");
        }
        return JsonDocument.Parse(line);
    }
}
