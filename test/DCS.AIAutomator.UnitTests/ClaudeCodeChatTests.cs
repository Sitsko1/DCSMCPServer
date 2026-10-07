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
    private const string PermissionRequest = """{"type":"control_request","request_id":"0d04cfcc-b378-42a9-b18e-bdee33b230dd","request":{"subtype":"can_use_tool","tool_name":"mcp__dcs-aiautomator__send_atc_instruction","mcp_server":{"name":"dcs-aiautomator","source":"dynamic"},"display_name":"Send Atc Instruction","input":{"aircraft_callsign":"Overlord 1-1","action":"Orbit"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"mcp__dcs-aiautomator__send_atc_instruction"}],"behavior":"allow","destination":"localSettings"}],"tool_use_id":"toolu_011mqCvUFGRQCKCB3nB6YHw4"}}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"I can't read your altitude.","session_id":"ed50cb96-a493-4c78-b84a-dd4e06849487","total_cost_usd":0.0176022}""";

    [Fact]
    public void Arguments_IsolateTheRunToThisAppsToolsAndServer()
    {
        string[] args = ClaudeCodeChat.Arguments(@"C:\x\mcp.json", resumeSessionId: null, confirmDcsChanges: true);

        Assert.Equal("-p", args[0]);
        AssertPair(args, "--input-format", "stream-json"); // the prompt goes in on stdin, not the command line
        AssertPair(args, "--output-format", "stream-json");
        AssertPair(args, "--permission-prompts", "host"); // the app answers permission prompts…
        AssertPair(args, "--permission-prompt-tool", "stdio"); // …on stdin/stdout, so no approval tool is exposed
        Assert.Contains("--include-partial-messages", args);
        AssertPair(args, "--tools", ""); // no built-in tools (Bash, Edit, …)
        Assert.Contains("--restricted", args); // no user/project settings: plugins, hooks
        Assert.Contains("--strict-mcp-config", args); // no other MCP servers
        AssertPair(args, "--mcp-config", @"C:\x\mcp.json");
        Assert.Contains("--disable-slash-commands", args); // no skills
        // Confirmation on: only the read-only tools run without asking.
        AssertPair(args, "--allowedTools", "mcp__dcs-aiautomator__get_aircraft_state,mcp__dcs-aiautomator__list_ai_flights");
        AssertPair(args, "--system-prompt", ClaudeCodeChat.SystemPrompt);
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--append-system-prompt", args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Arguments_AnEmptySessionId_StartsANewChat_InsteadOfABrokenResume(string sessionId) =>
        Assert.DoesNotContain("--resume", ClaudeCodeChat.Arguments("mcp.json", sessionId, confirmDcsChanges: true));

    [Fact]
    public void Parse_AFailedResult_WithOnlyAnErrorsArray_CarriesThoseErrors()
    {
        // Real output for a bad --resume (Claude Code 2.1.288): "result" is null, the reason is in "errors".
        var done = Assert.IsType<ChatCompleted>(ClaudeCodeChat.Parse(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":null,"errors":["Error: --resume requires a valid session ID or session title when used with --print."]}"""));

        Assert.Equal("Error: --resume requires a valid session ID or session title when used with --print.", done.Error);
    }

    [Fact]
    public void Arguments_WithConfirmationOff_PreAllowTheDcsChangingTools()
    {
        string[] args = ClaudeCodeChat.Arguments("mcp.json", null, confirmDcsChanges: false);

        AssertPair(args, "--allowedTools",
            "mcp__dcs-aiautomator__get_aircraft_state,mcp__dcs-aiautomator__list_ai_flights,mcp__dcs-aiautomator__send_atc_instruction");
    }

    [Fact]
    public void Arguments_ResumeTheConversation_AndAppendExtraInstructions()
    {
        string[] args = ClaudeCodeChat.Arguments("mcp.json", "ed50cb96", confirmDcsChanges: true, extraSystemPrompt: "Lesson: be brief.");

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
        Assert.DoesNotContain(ClaudeCodeChat.Arguments("mcp.json", null, confirmDcsChanges: true), a => a.Contains(key));
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
    public void Parse_APermissionRequest_ForAToolThatIsntPreAllowed()
    {
        Assert.Equal(
            new ChatPermissionRequest("0d04cfcc-b378-42a9-b18e-bdee33b230dd", "toolu_011mqCvUFGRQCKCB3nB6YHw4", "send_atc_instruction",
                """{"aircraft_callsign":"Overlord 1-1","action":"Orbit"}"""),
            ClaudeCodeChat.Parse(PermissionRequest));
    }

    [Fact]
    public void PermissionResponse_Allow_EchoesTheInput_Deny_CarriesAMessage()
    {
        var request = (ChatPermissionRequest)ClaudeCodeChat.Parse(PermissionRequest)!;

        JsonElement allow = JsonDocument.Parse(ClaudeCodeChat.PermissionResponseJson(request, new ChatPermissionDecision(true))).RootElement;
        Assert.Equal("control_response", allow.GetProperty("type").GetString());
        JsonElement r = allow.GetProperty("response");
        Assert.Equal("success", r.GetProperty("subtype").GetString());
        Assert.Equal("0d04cfcc-b378-42a9-b18e-bdee33b230dd", r.GetProperty("request_id").GetString());
        Assert.Equal("allow", r.GetProperty("response").GetProperty("behavior").GetString());
        Assert.Equal("Overlord 1-1", r.GetProperty("response").GetProperty("updatedInput").GetProperty("aircraft_callsign").GetString());

        JsonElement deny = JsonDocument.Parse(ClaudeCodeChat.PermissionResponseJson(request, new ChatPermissionDecision(false, "Not now."))).RootElement
            .GetProperty("response").GetProperty("response");
        Assert.Equal("deny", deny.GetProperty("behavior").GetString());
        Assert.Equal("Not now.", deny.GetProperty("message").GetString());
    }

    [Fact]
    public void UserMessage_CarriesThePromptAsData()
    {
        const string prompt = "Vector \"Colt 1-1\" to 090\nnow";
        JsonElement m = JsonDocument.Parse(ClaudeCodeChat.UserMessageJson(prompt)).RootElement;

        Assert.Equal("user", m.GetProperty("type").GetString());
        Assert.Equal("user", m.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal(prompt, m.GetProperty("message").GetProperty("content").GetString());
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
