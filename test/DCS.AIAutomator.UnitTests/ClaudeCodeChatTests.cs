using System.Text.Json;

/// <summary>
/// The in-app chat's Claude Code invocation and stream parsing (#16). The sample lines are real
/// <c>claude -p --output-format stream-json</c> output (Claude Code 2.1.288), trimmed to the fields used.
/// </summary>
public class ClaudeCodeChatTests
{
    private const string Init = """{"type":"system","subtype":"init","session_id":"ed50cb96-a493-4c78-b84a-dd4e06849487","tools":["mcp__dcs-aiautomator__get_aircraft_state","mcp__dcs-aiautomator__list_ai_flights","mcp__dcs-aiautomator__send_atc_instruction"],"mcp_servers":[{"name":"dcs-aiautomator","status":"connected","source":"dynamic"}]}""";
    private const string ToolUse = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01NJLSUqb4h1KBiArriwR9Lz","name":"mcp__dcs-aiautomator__get_aircraft_state","input":{},"caller":{"type":"direct"}}]}}""";
    private const string ToolResult = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01NJLSUqb4h1KBiArriwR9Lz","type":"tool_result","content":[{"type":"text","text":"No active aircraft: DCS is not connected."}]}]}}""";
    private const string Delta = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"I can't read your altitude"}}}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"I can't read your altitude.","session_id":"ed50cb96-a493-4c78-b84a-dd4e06849487","total_cost_usd":0.0176022}""";

    [Fact]
    public void Arguments_IsolateTheRunToThisAppsToolsAndServer()
    {
        string[] args = ClaudeCodeChat.Arguments("what's my altitude?", @"C:\x\mcp.json", resumeSessionId: null);

        Assert.Equal(["-p", "what's my altitude?"], args[..2]);
        AssertPair(args, "--output-format", "stream-json");
        Assert.Contains("--include-partial-messages", args);
        AssertPair(args, "--tools", ""); // no built-in tools (Bash, Edit, …)
        Assert.Contains("--restricted", args); // no user/project settings: plugins, hooks
        Assert.Contains("--strict-mcp-config", args); // no other MCP servers
        AssertPair(args, "--mcp-config", @"C:\x\mcp.json");
        Assert.Contains("--disable-slash-commands", args); // no skills
        AssertPair(args, "--allowedTools",
            "mcp__dcs-aiautomator__get_aircraft_state,mcp__dcs-aiautomator__list_ai_flights,mcp__dcs-aiautomator__send_atc_instruction");
        AssertPair(args, "--system-prompt", ClaudeCodeChat.SystemPrompt);
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--append-system-prompt", args);
    }

    [Fact]
    public void Arguments_ResumeTheConversation_AndAppendExtraInstructions()
    {
        string[] args = ClaudeCodeChat.Arguments("and now?", "mcp.json", "ed50cb96", extraSystemPrompt: "Lesson: be brief.");

        AssertPair(args, "--resume", "ed50cb96");
        AssertPair(args, "--append-system-prompt", "Lesson: be brief.");
    }

    [Fact]
    public void TheApiKey_IsOnlyInTheConfigFile_NeverInTheArguments()
    {
        const string key = "ApiKeySentinel_0123456789";
        string config = ClaudeCodeChat.McpConfigJson("http://127.0.0.1:5270/mcp", key);

        JsonElement server = JsonDocument.Parse(config).RootElement.GetProperty("mcpServers").GetProperty("dcs-aiautomator");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:5270/mcp", server.GetProperty("url").GetString());
        Assert.Equal($"Bearer {key}", server.GetProperty("headers").GetProperty("Authorization").GetString());
        // Command lines are readable by other local processes; the key goes in a private file.
        Assert.DoesNotContain(ClaudeCodeChat.Arguments("hi", "mcp.json", null), a => a.Contains(key));
    }

    [Fact]
    public void Parse_Init_GivesTheSessionAndWhetherThisAppsServerConnected()
    {
        var started = Assert.IsType<ChatSessionStarted>(ClaudeCodeChat.Parse(Init));

        Assert.Equal("ed50cb96-a493-4c78-b84a-dd4e06849487", started.SessionId);
        Assert.Equal(3, started.Tools.Count);
        Assert.True(started.ServerConnected);
        Assert.False(Assert.IsType<ChatSessionStarted>(ClaudeCodeChat.Parse(Init.Replace("\"connected\"", "\"failed\""))).ServerConnected);
    }

    [Fact]
    public void Parse_TextDelta_ToolCall_ToolResult_AndResult()
    {
        Assert.Equal(new ChatTextDelta("I can't read your altitude"), ClaudeCodeChat.Parse(Delta));
        Assert.Equal(new ChatToolCall("toolu_01NJLSUqb4h1KBiArriwR9Lz", "get_aircraft_state", "{}"), ClaudeCodeChat.Parse(ToolUse));
        Assert.Equal(new ChatToolResult("toolu_01NJLSUqb4h1KBiArriwR9Lz", "No active aircraft: DCS is not connected.", false), ClaudeCodeChat.Parse(ToolResult));
        Assert.Equal(new ChatCompleted(null, 0.0176022), ClaudeCodeChat.Parse(Result));
    }

    [Fact]
    public void Parse_AFailedResult_CarriesItsMessage()
    {
        var done = Assert.IsType<ChatCompleted>(ClaudeCodeChat.Parse(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Not logged in"}"""));

        Assert.Equal("Not logged in", done.Error);
    }

    [Fact]
    public void Parse_AToolResultWithAPlainStringAndAnErrorFlag()
    {
        Assert.Equal(new ChatToolResult("t1", "Error: denied", true), ClaudeCodeChat.Parse(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"Error: denied","is_error":true}]}}"""));
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"hook_started"}""")]
    [InlineData("""{"type":"system","subtype":"status"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"message_start"}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"already streamed"}]}}""")]
    [InlineData("""{"type":"rate_limit_event"}""")]
    [InlineData("not json")]
    [InlineData("""{"type":"system","subtype":7}""")] // an unexpected field type is skipped, not a crash
    public void Parse_LinesTheChatDoesntShow_AreNull(string line) =>
        Assert.Null(ClaudeCodeChat.Parse(line));

    private static void AssertPair(string[] args, string flag, string value)
    {
        int i = Array.IndexOf(args, flag);
        Assert.True(i >= 0 && i + 1 < args.Length, $"{flag} missing");
        Assert.Equal(value, args[i + 1]);
    }
}
