using System.Net;
using System.Net.Sockets;
using System.Text;
using DCS.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Drives DcsConnection against a real loopback listener standing in for the DCS Hooks script.
/// </summary>
public class DcsConnectionTests : IAsyncLifetime
{
    // Short stand-in for the real 5 s not-responding timeout, so tests stay fast.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    private const string MissionLine = """{"missionActive":true,"aircraft":"F/A-18C","ownship":{"lat":1,"lon":2}}""";

    private readonly TcpListener _fakeDcs = new(IPAddress.Loopback, 0);
    private readonly BridgeStatus _status = new();
    private DcsConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _fakeDcs.Start();
        int port = ((IPEndPoint)_fakeDcs.LocalEndpoint).Port;
        _connection = new DcsConnection(NullLogger<DcsConnection>.Instance, _status, "127.0.0.1", port, Timeout);
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
        using (TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync())
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
        using TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
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
        using TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
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
        using TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
        await Send(dcsSide, """{"missionActive":false}""");
        await WaitUntil(() => _status.DcsConnected);

        await Task.Delay(Timeout * 4);

        Assert.False(_status.DcsNotResponding);
    }

    [Fact]
    public async Task Resume_ClearsPaused_AndMissionEnd_ClearsPaused()
    {
        using TcpClient dcsSide = await _fakeDcs.AcceptTcpClientAsync();
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
