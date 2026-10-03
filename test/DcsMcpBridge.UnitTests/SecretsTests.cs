using DCS.Scripting;

public class SecretsTests
{
    [Fact]
    public void NewSecret_Is256BitBase64Url()
    {
        string secret = Secrets.NewSecret();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret); // 32 random bytes, base64url, no padding
    }

    [Fact]
    public void NewSecret_IsDifferentEachTime() =>
        Assert.Equal(100, Enumerable.Range(0, 100).Select(_ => Secrets.NewSecret()).Distinct().Count());

    [Theory]
    [InlineData("Bearer abc", "abc", true)]
    [InlineData("bearer abc", "abc", true)]   // scheme is case-insensitive
    [InlineData("Bearer abd", "abc", false)]
    [InlineData("Bearer abcd", "abc", false)]  // length differs
    [InlineData("abc", "abc", false)]          // no scheme
    [InlineData("Basic abc", "abc", false)]
    [InlineData("", "abc", false)]
    [InlineData(null, "abc", false)]
    public void BearerMatches(string? header, string key, bool expected) =>
        Assert.Equal(expected, Secrets.BearerMatches(header, key));
}

public class ClaudeCodeRegistrationTests
{
    private const string Url = "http://127.0.0.1:5270/mcp";
    private const string Key = "k3y_SECRET-value";

    [Fact]
    public void AddArguments_RegisterAUserScopedHttpServer_WithTheBearerHeaderLast()
    {
        // --header is variadic in the claude CLI, so it must come after <name> <url>.
        Assert.Equal(
            ["mcp", "add", "--transport", "http", "--scope", "user", "dcs-aiautomator", Url, "--header", $"Authorization: Bearer {Key}"],
            ClaudeCodeRegistration.AddArguments(Url, Key));
    }

    [Fact]
    public void GetArguments_QueryOurEntry() =>
        // Exit code 0 = registered, 1 = not (checked against Claude Code 2.1.283).
        Assert.Equal(["mcp", "get", "dcs-aiautomator"], ClaudeCodeRegistration.GetArguments());

    [Fact]
    public void RemoveArguments_RemoveOnlyOurUserScopedEntry() =>
        Assert.Equal(["mcp", "remove", "--scope", "user", "dcs-aiautomator"], ClaudeCodeRegistration.RemoveArguments());

    [Fact]
    public void CopyableCommand_IsPasteableIntoATerminal()
    {
        Assert.Equal(
            $"claude mcp add --transport http --scope user dcs-aiautomator {Url} --header \"Authorization: Bearer {Key}\"",
            ClaudeCodeRegistration.CopyableCommand(Url, Key));
    }

    [Fact]
    public void Redact_RemovesTheKeyFromCliOutput()
    {
        string output = $"Error: server dcs-aiautomator already has header Authorization: Bearer {Key}";

        string redacted = ClaudeCodeRegistration.Redact(output, Key);

        Assert.DoesNotContain(Key, redacted);
        Assert.Contains("***", redacted);
    }

    [Theory]
    [InlineData("No MCP server named \"dcs-aiautomator\" in user scope", true)]
    [InlineData("No MCP server named \"dcs-aiautomator\". Configured servers: a, b", true)]
    [InlineData("Failed to write config: EACCES", false)]
    [InlineData("", false)]
    public void IsNotRegisteredOutput(string output, bool expected) =>
        Assert.Equal(expected, ClaudeCodeRegistration.IsNotRegisteredOutput(output));
}
