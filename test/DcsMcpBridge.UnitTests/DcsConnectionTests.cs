using System.Net;
using System.Net.Sockets;
using System.Text;
using DCS.Scripting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Drives DcsConnection against a real loopback listener standing in for the DCS Hooks script.
/// </summary>
public class DcsConnectionTests : IAsyncLifetime
{
    // Short stand-in for the real 5 s not-responding timeout, so tests stay fast.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    private const string Secret = "TestLinkSecret_0123456789";
    private const string MissionLine = """{"missionActive":true,"aircraft":"F/A-18C","ownship":{"lat":1,"lon":2}}""";

    private readonly TcpListener _fakeDcs = new(IPAddress.Loopback, 0);
    private readonly BridgeStatus _status = new();
    private readonly ListLogger _log = new();
    private DcsConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _fakeDcs.Start();
        int port = ((IPEndPoint)_fakeDcs.LocalEndpoint).Port;
        _connection = new DcsConnection(_log, _status, "127.0.0.1", port, Secret, Timeout, dcsScriptLogger: _log);
        await _connection.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _connection.StopAsync(CancellationToken.None);
        _connection.Dispose();
        _fakeDcs.Stop();
    }

    [Fact]
    public async Task ReportsDisconnected_WhenDcsClosesTheConnection()
    {
        // e.g. DCS killed: the OS closes its socket, so the app's read sees end-of-stream (not an
        // exception) — and TcpClient.Connected still reports true afterwards.
        using (TcpClient dcsSide = await AcceptAuthenticatedAsync())
        {
            await Send(dcsSide, MissionLine);
            await WaitUntil(() => _status.DcsConnected && _status.CurrentMission is not null);
        } // closing the DCS side == DCS going away

        await WaitUntil(() => !_status.DcsConnected);
        Assert.Null(_status.CurrentMission);
        Assert.Null(_status.Aircraft);
    }

    [Fact]
    public async Task GoesNotResponding_WhenSilentMidMission_AndRecoversOnTheSameSocket()
    {
        using TcpClient dcsSide = await AcceptAuthenticatedAsync();
        await Send(dcsSide, MissionLine);
        await WaitUntil(() => _status.CurrentMission is not null);

        await WaitUntil(() => _status.DcsNotResponding); // DCS "hangs": no more lines

        // Keep waiting (maintainer decision): a long silence must never drop the connection.
        await Task.Delay(Timeout * 4);
        Assert.True(_status.DcsConnected);
        Assert.True(_status.DcsNotResponding);
        Assert.NotNull(_status.CurrentMission); // last known state is held, not cleared

        await Send(dcsSide, """{"heartbeat":true}""");
        await WaitUntil(() => !_status.DcsNotResponding);
        Assert.True(_status.DcsConnected);
        Assert.False(_fakeDcs.Pending(), "recovery must reuse the socket, not reconnect");
    }

    [Fact]
    public async Task NotResponding_IsNotRaised_WhilePaused()
    {
        using TcpClient dcsSide = await AcceptAuthenticatedAsync();
        await Send(dcsSide, MissionLine);
        await Send(dcsSide, """{"paused":true}""");
        await WaitUntil(() => _status.DcsPaused);

        await Task.Delay(Timeout * 4);

        Assert.False(_status.DcsNotResponding);
        Assert.NotNull(_status.CurrentMission); // a pause line isn't a "no mission" report
    }

    [Fact]
    public async Task NotResponding_IsNotRaised_WithoutAnActiveMission()
    {
        // e.g. DCS sitting in the menus, where no Hooks callback runs to send anything.
        using TcpClient dcsSide = await AcceptAuthenticatedAsync();
        await Send(dcsSide, """{"missionActive":false}""");
        await WaitUntil(() => _status.DcsConnected);

        await Task.Delay(Timeout * 4);

        Assert.False(_status.DcsNotResponding);
    }

    [Fact]
    public async Task ForwardsDcsScriptErrors_AsStatusEvents_WithoutTouchingTheMission()
    {
        var errors = new List<string>();
        _status.DcsScriptError += (_, message) => { lock (errors) errors.Add(message); };

        using TcpClient dcsSide = await AcceptAuthenticatedAsync();
        await Send(dcsSide, MissionLine);
        await WaitUntil(() => _status.CurrentMission is not null);
        await Send(dcsSide, """{"log":{"level":"info","message":"hooks loaded"}}""");
        await Send(dcsSide, """{"log":{"level":"error","message":"frame error: boom"}}""");

        await WaitUntil(() => { lock (errors) return errors.Count > 0; });
        await Task.Delay(100);
        lock (errors) Assert.Equal(["frame error: boom"], errors); // info-level lines aren't errors
        Assert.NotNull(_status.CurrentMission);
    }

    [Fact]
    public async Task Resume_ClearsPaused_AndMissionEnd_ClearsPaused()
    {
        using TcpClient dcsSide = await AcceptAuthenticatedAsync();
        await Send(dcsSide, MissionLine);
        await Send(dcsSide, """{"paused":true}""");
        await WaitUntil(() => _status.DcsPaused);

        await Send(dcsSide, """{"paused":false}""");
        await WaitUntil(() => !_status.DcsPaused);

        await Send(dcsSide, """{"paused":true}""");
        await WaitUntil(() => _status.DcsPaused);
        await Send(dcsSide, """{"missionActive":false}""");
        await WaitUntil(() => !_status.DcsPaused && _status.CurrentMission is null);
    }

    [Fact]
    public async Task SendsTheLinkSecretFirst_AndIsConnectedOnlyAfterAuthOk()
    {
        using TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
        Assert.Equal($"AUTH {Secret}", await ReadLine(dcsSide));

        await Task.Delay(Timeout);
        Assert.False(_status.DcsConnected, "not connected until DCS accepts the secret");

        await Send(dcsSide, """{"authOk":true}""");
        await WaitUntil(() => _status.DcsConnected);
        Assert.False(_status.DcsAuthFailed);
    }

    [Fact]
    public async Task SendsTheLinkSecretAgain_OnEveryReconnect()
    {
        using (TcpClient first = await AcceptAuthenticatedAsync()) { } // DCS goes away
        await WaitUntil(() => !_status.DcsConnected);

        using TcpClient second = await _fakeDcs.AcceptTcpClientAsync();
        Assert.Equal($"AUTH {Secret}", await ReadLine(second));
    }

    [Fact]
    public async Task AuthError_ShowsAuthFailed_NotConnected()
    {
        using TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
        await ReadLine(dcsSide);
        await Send(dcsSide, """{"authError":true}""");

        await WaitUntil(() => _status.DcsAuthFailed);
        Assert.False(_status.DcsConnected);
    }

    [Fact]
    public async Task DataBeforeAuthOk_FromAnOutdatedScript_IsAuthFailed_AndIgnored()
    {
        // A script deployed before auth existed never answers the AUTH line; it just streams.
        using TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
        await ReadLine(dcsSide);
        await Send(dcsSide, MissionLine);

        await WaitUntil(() => _status.DcsAuthFailed);
        Assert.False(_status.DcsConnected);
        Assert.Null(_status.CurrentMission); // unauthenticated data isn't trusted
    }

    [Fact]
    public async Task AuthFailed_ClearsOnceDcsAcceptsTheSecret()
    {
        using (TcpClient rejected = await _fakeDcs.AcceptTcpClientAsync())
        {
            await ReadLine(rejected);
            await Send(rejected, """{"authError":true}""");
            await WaitUntil(() => _status.DcsAuthFailed);
        }

        using TcpClient accepted = await AcceptAuthenticatedAsync(); // e.g. after redeploying
        await WaitUntil(() => _status.DcsConnected && !_status.DcsAuthFailed);
    }

    [Fact]
    public async Task TheLinkSecret_IsNeverLogged()
    {
        using (TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync())
        {
            await ReadLine(dcsSide);
            await Send(dcsSide, """{"authError":true}""");
            await WaitUntil(() => _status.DcsAuthFailed);
        }
        await WaitUntil(() => _log.Messages.Count > 0);

        Assert.DoesNotContain(_log.Messages, m => m.Contains(Secret));
    }

    // Accepts the app's connection and completes the handshake as a current Hooks script would.
    private async Task<TcpClient> AcceptAuthenticatedAsync()
    {
        TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
        Assert.Equal($"AUTH {Secret}", await ReadLine(dcsSide));
        await Send(dcsSide, """{"authOk":true}""");
        return dcsSide;
    }

    // Byte-at-a-time so nothing past the line is consumed from the socket.
    private static async Task<string> ReadLine(TcpClient dcsSide)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        NetworkStream stream = dcsSide.GetStream();
        while (await stream.ReadAsync(one) == 1 && one[0] != (byte)'\n') bytes.Add(one[0]);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task Send(TcpClient dcsSide, string line) =>
        await dcsSide.GetStream().WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 5 s");
            await Task.Delay(20);
        }
    }
}

/// <summary>Captures formatted log messages (with exceptions) for "never logged" assertions.</summary>
internal sealed class ListLogger : ILogger<DcsConnection>
{
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToList(); } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_messages) _messages.Add(formatter(state, exception) + " " + exception);
    }
}
