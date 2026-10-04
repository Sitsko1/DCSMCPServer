using System.Text.Json;
using System.Text.Json.Nodes;

public class FakeDcsConnection : IDcsConnection
{
    /// <summary>The answer to any command without its own entry in <see cref="Responses"/>.</summary>
    public DcsCommandResult Result { get; set; } = new(true);
    public Dictionary<string, DcsCommandResult> Responses { get; } = new();
    public List<(string Cmd, JsonObject Args)> Sent { get; } = new();
    public string? LastCmd => Sent.Count > 0 ? Sent[^1].Cmd : null;
    public JsonObject? LastArgs => Sent.Count > 0 ? Sent[^1].Args : null;

    public JsonObject? ArgsOf(string cmd) => Sent.LastOrDefault(s => s.Cmd == cmd).Args;

    public void RespondWithData(string cmd, string json) =>
        Responses[cmd] = new DcsCommandResult(true, Data: JsonDocument.Parse(json).RootElement);

    public Task<DcsCommandResult> SendCommandAsync(string cmd, JsonObject args, CancellationToken cancellationToken = default)
    {
        Sent.Add((cmd, args));
        return Task.FromResult(Responses.GetValueOrDefault(cmd, Result));
    }
}
