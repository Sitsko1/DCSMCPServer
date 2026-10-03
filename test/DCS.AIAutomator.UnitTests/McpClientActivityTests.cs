
public class McpClientActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly McpClientActivity _activity = new();

    [Fact]
    public void NothingSeen_IsNoClients()
    {
        McpClientSnapshot s = _activity.Snapshot(T0);

        Assert.Equal(McpClientState.NoClients, s.State);
        Assert.Empty(s.ClientNames);
        Assert.Null(s.LastSeenUtc);
    }

    [Fact]
    public void ARecentRequest_IsActive_WithTheClientName()
    {
        _activity.RecordRequest("claude-code", T0);

        McpClientSnapshot s = _activity.Snapshot(T0.AddSeconds(59));

        Assert.Equal(McpClientState.Active, s.State);
        Assert.Equal(["claude-code"], s.ClientNames);
        Assert.Equal(T0, s.LastSeenUtc);
    }

    [Fact]
    public void ARequestWithoutAName_IsActive_ButAddsNoName()
    {
        _activity.RecordRequest(null, T0);

        McpClientSnapshot s = _activity.Snapshot(T0);

        Assert.Equal(McpClientState.Active, s.State);
        Assert.Empty(s.ClientNames);
    }

    [Fact]
    public void AfterTheActiveWindow_IsIdle()
    {
        _activity.RecordRequest("claude-code", T0);

        Assert.Equal(McpClientState.Idle, _activity.Snapshot(T0.AddSeconds(61)).State);
    }

    [Fact]
    public void ClientNames_AreDistinct_MostRecentFirst()
    {
        _activity.RecordRequest("claude-code", T0);
        _activity.RecordRequest("claude-ai", T0.AddSeconds(1));
        _activity.RecordRequest("claude-code", T0.AddSeconds(2));
        _activity.RecordRequest("claude-ai", T0.AddSeconds(3));

        McpClientSnapshot s = _activity.Snapshot(T0.AddSeconds(4));

        Assert.Equal(["claude-ai", "claude-code"], s.ClientNames);
        Assert.Equal(T0.AddSeconds(3), s.LastSeenUtc);
    }

    [Fact]
    public void ARecentRejection_IsAuthFailed()
    {
        _activity.RecordAuthFailure(T0);

        Assert.Equal(McpClientState.AuthFailed, _activity.Snapshot(T0.AddSeconds(30)).State);
    }

    [Fact]
    public void ARejection_AfterASuccess_IsAuthFailed()
    {
        _activity.RecordRequest("claude-code", T0);
        _activity.RecordAuthFailure(T0.AddSeconds(1));

        Assert.Equal(McpClientState.AuthFailed, _activity.Snapshot(T0.AddSeconds(2)).State);
    }

    [Fact]
    public void ASuccess_AfterARejection_ClearsAuthFailed()
    {
        _activity.RecordAuthFailure(T0);
        _activity.RecordRequest("claude-code", T0.AddSeconds(1));

        Assert.Equal(McpClientState.Active, _activity.Snapshot(T0.AddSeconds(2)).State);
    }

    [Fact]
    public void AnOldRejection_FallsBackToIdleOrNoClients()
    {
        _activity.RecordAuthFailure(T0);
        Assert.Equal(McpClientState.NoClients, _activity.Snapshot(T0.AddSeconds(61)).State);

        _activity.RecordRequest("claude-code", T0.AddSeconds(-10));
        Assert.Equal(McpClientState.Idle, _activity.Snapshot(T0.AddSeconds(61)).State);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        _activity.RecordRequest("claude-code", T0);
        _activity.RecordAuthFailure(T0);

        _activity.Reset();

        Assert.Equal(McpClientState.NoClients, _activity.Snapshot(T0).State);
        Assert.Empty(_activity.Snapshot(T0).ClientNames);
    }

    [Fact]
    public void Changed_IsRaisedOnEveryRecord()
    {
        int raised = 0;
        _activity.Changed += (_, _) => raised++;

        _activity.RecordRequest("claude-code", T0);
        _activity.RecordAuthFailure(T0);
        _activity.Reset();

        Assert.Equal(3, raised);
    }

    [Fact]
    public void BridgeStatus_RaisesChanged_WhenClientActivityIsRecorded()
    {
        var status = new BridgeStatus();
        int raised = 0;
        status.Changed += (_, _) => raised++;

        status.McpClients.RecordRequest("claude-code", T0);

        Assert.Equal(1, raised);
    }
}
