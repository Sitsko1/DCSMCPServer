using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Owns the persistent TCP connection to DCS World's Export.lua socket. Registered as both a
/// singleton (so tool classes can inject it to send Lua) and a hosted service (so its connect
/// loop runs for the app's lifetime) — see Program.cs.
/// </summary>
public sealed class DcsConnection : BackgroundService, IDcsConnection
{
    private readonly ILogger<DcsConnection> _logger;
    private readonly string _dcsIp;
    private readonly int _dcsPort;

    private TcpClient? _client;
    private NetworkStream? _stream;

    public DcsConnection(ILogger<DcsConnection> logger, string dcsIp = "127.0.0.1", int dcsPort = 1024)
    {
        _logger = logger;
        _dcsIp = dcsIp;
        _dcsPort = dcsPort;
    }

    /// <summary>
    /// Directly pushes raw Lua code over the socket to be executed inside DCS.
    /// </summary>
    public bool SendLuaCommand(string luaCode)
    {
        if (_client == null || !_client.Connected || _stream == null) return false;
        try
        {
            if (!luaCode.EndsWith("\n")) luaCode += "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(luaCode);
            _stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write Lua command to DCS stream socket.");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_client == null || !_client.Connected)
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync(_dcsIp, _dcsPort, stoppingToken);
                    _stream = _client.GetStream();
                    _logger.LogDebug("Connected to DCS socket.");
                }

                using var reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
                // ponytail: telemetry lines are drained but discarded — no tool reads them yet.
                // Add a cached-latest-line property here if a future tool needs to read DCS state back.
                while (!stoppingToken.IsCancellationRequested && await reader.ReadLineAsync(stoppingToken) is not null)
                {
                }
            }
            catch (Exception ex)
            {
                CleanConnection();
                _logger.LogWarning("DCS connection disrupted. Retrying in 3 seconds... Details: {Message}", ex.Message);
                try { await Task.Delay(3000, stoppingToken); } catch (TaskCanceledException) { break; }
            }
        }
    }

    private void CleanConnection()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _client = null;
        _stream = null;
    }

    public override void Dispose()
    {
        CleanConnection();
        base.Dispose();
    }
}
