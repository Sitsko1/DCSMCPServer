using System.Net;
using System.Net.Sockets;
using System.Text;
using DCS.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Drives DcsConnection against a real loopback listener standing in for the DCS Hooks script.
/// </summary>
public class DcsConnectionTests
{
    [Fact]
    public async Task ReportsDisconnected_WhenDcsClosesTheConnection()
    {
        // e.g. DCS killed: the OS closes its socket, so the app's read sees end-of-stream (not an
        // exception) — and TcpClient.Connected still reports true afterwards.
        var fakeDcs = new TcpListener(IPAddress.Loopback, 0);
        fakeDcs.Start();
        int port = ((IPEndPoint)fakeDcs.LocalEndpoint).Port;

        var status = new BridgeStatus();
        using var connection = new DcsConnection(NullLogger<DcsConnection>.Instance, status, "127.0.0.1", port);
        await connection.StartAsync(CancellationToken.None);
        try
        {
            using (TcpClient dcsSide = await fakeDcs.AcceptTcpClientAsync())
            {
                byte[] line = Encoding.UTF8.GetBytes("""{"missionActive":true,"aircraft":"F/A-18C"}""" + "\n");
                await dcsSide.GetStream().WriteAsync(line);
                await WaitUntil(() => status.DcsConnected && status.CurrentMission is not null);
            } // closing the DCS side == DCS going away

            await WaitUntil(() => !status.DcsConnected);
            Assert.Null(status.CurrentMission);
        }
        finally
        {
            await connection.StopAsync(CancellationToken.None);
            fakeDcs.Stop();
        }
    }

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
