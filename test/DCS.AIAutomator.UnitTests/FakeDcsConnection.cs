using System.Text.Json.Nodes;

public class FakeDcsConnection : IDcsConnection
{
    public DcsCommandResult Result { get; set; } = new(true);
    public string? LastCmd { get; private set; }
    public JsonObject? LastArgs { get; private set; }

    public Task<DcsCommandResult> SendCommandAsync(string cmd, JsonObject args, CancellationToken cancellationToken = default)
    {
        LastCmd = cmd;
        LastArgs = args;
        return Task.FromResult(Result);
    }
}
