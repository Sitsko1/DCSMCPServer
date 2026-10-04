using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DCS.AIAutomator.Core;

/// <summary>
/// Owns the persistent TCP connection to the DCS Hooks script's socket. Registered as both a
/// singleton (so tool classes can inject it to send commands) and a hosted service (so its connect
/// loop runs for the app's lifetime) — see DcsScriptingServiceCollectionExtensions.
/// </summary>
public sealed class DcsConnection : BackgroundService, IDcsConnection
{
    private readonly ILogger<DcsConnection> _logger;
    private readonly ILogger _dcsScriptLogger; // the Hooks script's own forwarded messages ("DCS" source context)
    private readonly BridgeStatus _status;
    private readonly string _dcsIp;
    private readonly int _dcsPort;
    private readonly TimeSpan _notRespondingTimeout;
    private readonly string _dcsLinkSecret;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private volatile bool _authenticated; // the Hooks script accepted our link secret on this connection
    private long _lastLineTicks = Environment.TickCount64; // written by the read loop, read by the watchdog
    private readonly TimeSpan _commandTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // tools send concurrently; lines must not interleave
    private long _nextCommandId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<DcsCommandResult>> _pendingCommands = new();

    /// <summary>
    /// How long a connected, mid-mission, unpaused DCS may stay silent before it's reported as
    /// not responding. The Hooks script heartbeats ~1/s, so this tolerates a few missed beats.
    /// </summary>
    public static readonly TimeSpan DefaultNotRespondingTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a command waits for its result. The script drains commands every frame, so a live
    /// DCS answers within a frame or two; silence this long means it's paused, hung or in the menus.
    /// </summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(5);

    public DcsConnection(
        ILogger<DcsConnection> logger,
        BridgeStatus status,
        string dcsIp = "127.0.0.1",
        int dcsPort = 1024,
        string dcsLinkSecret = "",
        TimeSpan? notRespondingTimeout = null,
        ILogger? dcsScriptLogger = null,
        TimeSpan? commandTimeout = null)
    {
        if (!Secrets.IsWellFormed(dcsLinkSecret))
        {
            throw new ArgumentException("A well-formed DCS link secret is required.", nameof(dcsLinkSecret));
        }
        _dcsLinkSecret = dcsLinkSecret;
        _logger = logger;
        _dcsScriptLogger = dcsScriptLogger ?? logger;
        _status = status;
        _dcsIp = dcsIp;
        _dcsPort = dcsPort;
        _notRespondingTimeout = notRespondingTimeout ?? DefaultNotRespondingTimeout;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    public async Task<DcsCommandResult> SendCommandAsync(string cmd, JsonObject args, CancellationToken cancellationToken = default)
    {
        NetworkStream? stream = _stream;
        if (stream is null || !_authenticated) return DcsCommandResult.Failed("DCS isn't connected.");

        long id = Interlocked.Increment(ref _nextCommandId);
        var reply = new TaskCompletionSource<DcsCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[id] = reply;
        try
        {
            // cmd and id set last, so an args key can't override them. Never logged (may carry user text).
            JsonObject command = args.DeepClone().AsObject();
            command["cmd"] = cmd;
            command["id"] = id;
            byte[] line = Encoding.UTF8.GetBytes(command.ToJsonString() + "\n");

            await _writeLock.WaitAsync(cancellationToken);
            try { await stream.WriteAsync(line, cancellationToken); }
            finally { _writeLock.Release(); }

            return await reply.Task.WaitAsync(_commandTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return DcsCommandResult.Failed($"DCS didn't answer within {_commandTimeout.TotalSeconds:0.#} s (paused, hung or in the menus?).");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogWarning("Failed to send a {Command} command to DCS: {Reason}", cmd, ex.Message);
            return DcsCommandResult.Failed("Lost the connection to DCS while sending.");
        }
        finally
        {
            _pendingCommands.TryRemove(id, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task watchdog = RunNotRespondingWatchdogAsync(stoppingToken);
        await RunConnectionLoopAsync(stoppingToken);
        await watchdog;
    }

    private async Task RunConnectionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool tcpConnected = false;
            try
            {
                if (_client == null || !_client.Connected)
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync(_dcsIp, _dcsPort, stoppingToken);
                    tcpConnected = true;
                    _stream = _client.GetStream();
                    Interlocked.Exchange(ref _lastLineTicks, Environment.TickCount64);

                    // Handshake: the secret goes first, and DCS counts as connected only once the
                    // Hooks script answers authOk. (Never logged.)
                    _authenticated = false;
                    byte[] auth = Encoding.UTF8.GetBytes($"AUTH {_dcsLinkSecret}\n");
                    await _stream.WriteAsync(auth, stoppingToken);
                }
                tcpConnected = true;

                using var reader = new StreamReader(_stream!, Encoding.UTF8, leaveOpen: true);
                while (!stoppingToken.IsCancellationRequested && await reader.ReadLineAsync(stoppingToken) is string line)
                {
                    if (!DcsTelemetryParser.TryParse(line, out DcsLine? parsed))
                    {
                        if (_authenticated) MarkAlive(); // garbage still proves an authenticated DCS is alive
                        continue;
                    }

                    if (!_authenticated)
                    {
                        HandleHandshakeReply(parsed);
                        continue;
                    }

                    // Any line at all proves DCS is alive.
                    MarkAlive();

                    // Only mission reports touch mission/aircraft; heartbeat and pause lines
                    // would otherwise wipe them.
                    if (parsed.IsMissionReport)
                    {
                        _status.CurrentMission = parsed.Mission;
                        _status.Aircraft = parsed.Aircraft;
                        if (parsed.Mission is null) _status.DcsPaused = false; // mission over
                    }
                    if (parsed.Paused is bool paused) _status.DcsPaused = paused;
                    if (parsed.Log is { } entry) ForwardScriptLog(entry);
                    // A reply nobody waits for any more (it timed out) is dropped.
                    if (parsed.CommandResult is (long id, DcsCommandResult result) && _pendingCommands.TryGetValue(id, out var waiter))
                        waiter.TrySetResult(result);
                }

                // ReadLineAsync returned null: DCS closed the connection (e.g. the process was
                // killed). That's not an exception, and TcpClient.Connected still reports true (it
                // only reflects the last I/O), so without this the loop would spin on the dead
                // stream and the UI would keep showing "connected". Take the disconnect path.
                if (!stoppingToken.IsCancellationRequested)
                {
                    throw new IOException("DCS closed the connection.");
                }
            }
            catch (Exception ex)
            {
                // Warn once when an established connection drops; the every-3-s retries while DCS
                // isn't running at all would otherwise flood the log, so those are Debug only.
                if (_status.DcsConnected)
                {
                    _logger.LogWarning("DCS connection lost ({Reason}); retrying every 3 s", ex.Message);
                }
                else
                {
                    _logger.LogDebug("DCS not reachable ({Reason}); retrying in 3 s", ex.Message);
                }
                if (!tcpConnected)
                {
                    // DCS isn't there at all; an old auth/version failure no longer says anything
                    _status.DcsAuthFailed = false;
                    _status.DcsScriptOutdated = false;
                }
                CleanConnection();
                try { await Task.Delay(3000, stoppingToken); } catch (TaskCanceledException) { break; }
            }
        }
    }

    private void MarkAlive()
    {
        Interlocked.Exchange(ref _lastLineTicks, Environment.TickCount64);
        _status.LastTelemetryUtc = DateTimeOffset.UtcNow;
        _status.DcsNotResponding = false;
    }

    /// <summary>The first meaningful line on a new connection must be the script's auth reply.</summary>
    private void HandleHandshakeReply(DcsLine reply)
    {
        if (reply.AuthOk && reply.Protocol != LuaHooksScriptGenerator.ProtocolVersion)
        {
            // Authenticated, but it speaks another contract: fields would silently go missing or
            // commands fail. Same treatment as a script without the handshake.
            FlagScriptOutdated(reply.Protocol is int v
                ? $"protocol {v}, app expects {LuaHooksScriptGenerator.ProtocolVersion}"
                : "no protocol version");
            throw new IOException("DCS's Hooks script speaks another protocol version.");
        }
        else if (reply.AuthOk)
        {
            _authenticated = true;
            MarkAlive();
            _status.DcsAuthFailed = false;
            _status.DcsScriptOutdated = false;
            _status.DcsConnected = true;
            _logger.LogInformation("Connected to DCS at {DcsHost}:{DcsPort}", _dcsIp, _dcsPort);
        }
        else if (reply.AuthError)
        {
            FlagAuthFailed("DCS rejected the link secret; redeploy the Lua scripts and restart DCS");
            // The script closes the connection itself; the end-of-stream path then retries.
        }
        else
        {
            // Data without a handshake: a Hooks script deployed before auth existed. Don't trust
            // it, and don't stay connected to it.
            FlagScriptOutdated("no auth handshake");
            throw new IOException("DCS sent data without authenticating.");
        }
    }

    private void FlagAuthFailed(string reason)
    {
        if (!_status.DcsAuthFailed) _logger.LogWarning("DCS link not authenticated: {Reason}", reason); // once, not per retry
        _status.DcsAuthFailed = true;
    }

    private void FlagScriptOutdated(string reason)
    {
        if (!_status.DcsScriptOutdated) _logger.LogWarning("DCS's Hooks script is outdated ({Reason}); redeploy the Lua scripts and restart DCS", reason); // once, not per retry
        _status.DcsScriptOutdated = true;
    }

    private void CleanConnection()
    {
        _authenticated = false;
        // Commands in flight will never get a reply on a new connection.
        foreach (var waiter in _pendingCommands.Values)
            waiter.TrySetResult(DcsCommandResult.Failed("The connection to DCS was lost before it answered."));
        _stream?.Dispose();
        _client?.Dispose();
        _client = null;
        _stream = null;
        _status.DcsConnected = false;
        _status.DcsPaused = false;
        _status.DcsNotResponding = false;
        _status.CurrentMission = null;
        _status.Aircraft = null;
    }

    private void ForwardScriptLog(DcsLogEntry entry)
    {
        LogLevel level = entry.Level.ToLowerInvariant() switch
        {
            "error" => LogLevel.Error,
            "warning" or "warn" => LogLevel.Warning,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Information,
        };
        _dcsScriptLogger.Log(level, "DCS script: {DcsMessage}", entry.Message);
        if (entry.IsError) _status.RaiseDcsScriptError(entry.Message);
    }

    /// <summary>
    /// Flags a hung DCS: heartbeats are expected only while connected, mid-mission and unpaused
    /// (DCS runs no Hooks callbacks in the menus, and model-time telemetry stops while paused).
    /// Never closes the socket — a hang may recover, and the next line clears the flag.
    /// </summary>
    private async Task RunNotRespondingWatchdogAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_notRespondingTimeout.TotalMilliseconds / 4, 10, 250));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                bool expected = _status.DcsConnected && _status.CurrentMission is not null && !_status.DcsPaused;
                long silentMs = Environment.TickCount64 - Interlocked.Read(ref _lastLineTicks);
                if (expected && silentMs >= _notRespondingTimeout.TotalMilliseconds && !_status.DcsNotResponding)
                {
                    _status.DcsNotResponding = true;
                    _logger.LogWarning("DCS not responding: no data for {Seconds:0.0} s mid-mission.", silentMs / 1000.0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public override void Dispose()
    {
        CleanConnection();
        base.Dispose();
    }
}
