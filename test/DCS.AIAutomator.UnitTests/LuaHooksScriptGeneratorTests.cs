
public class LuaHooksScriptGeneratorTests
{
    private static readonly string Lua = LuaHooksScriptGenerator.Generate("127.0.0.1", 1024, "TestLinkSecret_0123456789");

    [Fact]
    public void Generate_InterpolatesHostAndPort()
    {
        Assert.Contains("host = \"127.0.0.1\"", Lua);
        Assert.Contains("port = 1024", Lua);
    }

    [Fact]
    public void Generate_RegistersHooksCallbacks_InsteadOfExportLuaGlobals()
    {
        Assert.Contains("setUserCallbacks(", Lua);
        Assert.Contains("onSimulationFrame", Lua);
        Assert.Contains("onSimulationStart", Lua);
        Assert.Contains("onSimulationStop", Lua);
        // Hooks scripts must not define Export.lua's globals — that was the old integration.
        Assert.DoesNotContain("function LuaExportStart", Lua);
        Assert.DoesNotContain("function LuaExportAfterNextFrame", Lua);
    }

    [Fact]
    public void Generate_ReadsMapAndMissionNameFromSimApi()
    {
        Assert.Contains("getMissionName()", Lua);
        Assert.Contains("getCurrentMission()", Lua);
        Assert.Contains(".theatre", Lua);
        Assert.Contains("\"missionName\"", Lua);
        Assert.Contains("\"terrain\"", Lua);
    }

    [Fact]
    public void Generate_ResolvesReadableAircraftNameFromDcsDatabase()
    {
        // Export calls live under the Export. namespace in the Hooks environment.
        Assert.Contains("Export.LoGetSelfData()", Lua);
        Assert.Contains("getUnitTypeAttribute(", Lua);
        Assert.Contains("\"DisplayName\"", Lua);
    }

    [Fact]
    public void Generate_ToleratesSimApiBeingNamedDcs()
    {
        // Older DCS versions expose the same API as DCS.* rather than Sim.*.
        Assert.Contains("local Sim = Sim or DCS", Lua);
    }

    [Fact]
    public void Generate_DoesNotCallNonexistentLoGetMissionInfo() =>
        Assert.DoesNotContain("LoGetMissionInfo", Lua);

    [Fact]
    public void Generate_SendsOwnshipStateFromExportApi()
    {
        Assert.Contains("\"ownship\"", Lua);
        foreach (string call in new[]
        {
            "Export.LoGetAltitudeAboveSeaLevel()", "Export.LoGetAltitudeAboveGroundLevel()",
            "Export.LoGetIndicatedAirSpeed()", "Export.LoGetTrueAirSpeed()", "Export.LoGetMachNumber()",
            "Export.LoGetVerticalVelocity()", "Export.LoGetMagneticYaw()", "Export.LoGetMCPState()",
            "LatLongAlt",
        })
        {
            Assert.Contains(call, Lua);
        }
    }

    [Fact]
    public void Generate_ReportsOnlyFailureFlags_NotPlainStateFlags()
    {
        Assert.Contains("\"LeftEngineFailure\"", Lua);
        Assert.Contains("\"FuelTankDamage\"", Lua);
        Assert.Contains("\"MasterWarning\"", Lua);
        Assert.DoesNotContain("AutopilotOn", Lua);
        Assert.DoesNotContain("CanopyOpen", Lua);
    }

    [Fact]
    public void Generate_ReportsFailureFlagsOnlyForFc3Aircraft()
    {
        // LoGetMCPState only reflects the simplified FC3 flight models. Verified live: an F/A-18C
        // with several triggered failures (engine, MC 2, generator, …) reports none of them.
        Assert.Contains("[\"F-15C\"] = true", Lua);
        Assert.Contains("[\"Su-27\"] = true", Lua);
        Assert.DoesNotContain("[\"FA-18C_hornet\"]", Lua); // full-fidelity: not an FC3 list entry
        Assert.Contains("mcpBridgeFc3Types[typeName]", Lua);
    }

    [Fact]
    public void Generate_ThrottlesTelemetry_ButDrainsCommandsEveryFrame()
    {
        Assert.Contains("TELEMETRY_INTERVAL = 0.2", Lua); // invariant culture, never "0,2"
        Assert.Contains("Export.LoGetModelTime()", Lua);

        string frame = Lua[Lua.IndexOf("function mcpBridgeCallbacks.onSimulationFrame")..];
        int readCommands = frame.IndexOf("mcpBridgeReadCommands()");
        int throttleCheck = frame.IndexOf("TELEMETRY_INTERVAL");
        Assert.True(readCommands >= 0 && readCommands < throttleCheck,
            "commands must be drained before (outside) the telemetry throttle");
    }

    [Fact]
    public void Generate_SendsHeartbeatOnRealTime_OutsideTheTelemetryThrottle()
    {
        Assert.Contains("HEARTBEAT_INTERVAL = 1", Lua);
        Assert.Contains("{\"heartbeat\":true}", Lua);
        // Real (wall-clock) time keeps advancing while paused; the telemetry throttle's model
        // time doesn't.
        Assert.Contains("Sim.getRealTime()", Lua);

        string frame = Lua[Lua.IndexOf("function mcpBridgeCallbacks.onSimulationFrame")..];
        int heartbeat = frame.IndexOf("mcpBridgeSendHeartbeat()");
        int throttle = frame.IndexOf("TELEMETRY_INTERVAL");
        Assert.True(heartbeat >= 0 && heartbeat < throttle, "heartbeat must not sit behind the telemetry throttle");
    }

    [Fact]
    public void Generate_ForwardsScriptLogMessagesToTheApp_RateLimitedAndBuffered()
    {
        Assert.Contains("log.write(\"DCSMcpBridge\"", Lua);       // still written to dcs.log
        Assert.Contains("{\"log\":{\"level\":", Lua);            // ...and sent to the app
        Assert.Contains("LOG_REPEAT_INTERVAL = 10", Lua);        // same message at most once per 10 s
        Assert.Contains("LOG_BUFFER_SIZE = 10", Lua);            // held until the app connects
        Assert.Contains("mcpBridgeFlushLogBuffer()", Lua);
    }

    [Fact]
    public void Generate_JsonEscapesControlCharacters()
    {
        // Lua error messages can contain tabs/newlines; raw control characters make invalid JSON.
        Assert.Contains("gsub('%c'", Lua);
        Assert.Contains(@"'\\u%04x'", Lua); // Lua source text: '\\u%04x' → JSON \u00XX
    }

    [Fact]
    public void Generate_NeverRunsWhatTheAppSendsAsLua()
    {
        Assert.DoesNotContain("loadstring", Lua);
        Assert.DoesNotContain("dostring(", Lua.Replace("net.dostring_in(", ""));
        Assert.Contains("net.json2lua(line)", Lua); // commands are decoded as data...
        Assert.Contains("mcpBridgeCommands[command.cmd]", Lua); // ...and dispatched to a fixed handler table
    }

    [Fact]
    public void Generate_AnswersEveryCommand_IncludingUnknownOnes()
    {
        Assert.Contains("\"unknown command: \"", Lua);
        Assert.Contains("{\"commandResult\":{\"id\":%d,\"ok\":true}", Lua);
        Assert.Contains("{\"commandResult\":{\"id\":%d,\"ok\":false,\"error\":\"%s\"}", Lua);
    }

    [Fact]
    public void Generate_MessageHandler_QuotesTheTextAsALuaLiteral_InMissionScripting()
    {
        string handler = FunctionBody("function mcpBridgeCommands.message(c)");
        Assert.Contains("type(c.text) ~= \"string\"", handler); // validated
        Assert.Contains("trigger.action.outText(%q, %d)", handler); // %q-quoted, never spliced in raw
        // Directly in the scripting state: a_do_script (via "mission") drops return values (verified live).
        Assert.Contains("pcall(net.dostring_in, \"scripting\", code)", Lua);
        Assert.DoesNotContain("a_do_script(", Lua);
        Assert.Contains($"local MAX_MESSAGE_SECONDS = {DcsCommands.MaxMessageSeconds}", Lua);
    }

    [Fact]
    public void Generate_ListFlights_ReturnsAirGroupsAsJsonData_FromMissionScripting()
    {
        string handler = FunctionBody("function mcpBridgeCommands.listFlights(c)");
        Assert.Contains("mcpBridgeRunInMission(LIST_FLIGHTS_CODE)", handler);
        // Display names come from the Hooks side's database lookup, as for the aircraft readout
        // (mission scripting's getDesc().displayName gave "e-2c hawkeye"-style names live).
        Assert.Contains("mcpBridgeDisplayName(typeName)", handler);
        Assert.True(Lua.IndexOf("local function mcpBridgeDisplayName(") < Lua.IndexOf("function mcpBridgeCommands.listFlights("),
            "listFlights must be defined below mcpBridgeDisplayName, or the local isn't visible to it");
        foreach (string call in new[]
        {
            "coalition.getGroups(side, category)", "Group.Category.AIRPLANE", "Group.Category.HELICOPTER",
            "lead:getCallsign()", "u:getPlayerName()", "coord.LOtoLL(p)", "lead:getTypeName()",
        })
        {
            Assert.Contains(call, Lua);
        }
        Assert.Contains("return \"ok [\" .. table.concat(out, \",\") .. \"]\"", Lua);
        Assert.Contains("result:sub(1, 3) == \"ok \"", Lua); // "ok <json>" = success with data
        Assert.Contains("{\"commandResult\":{\"id\":%d,\"ok\":true,\"data\":", Lua);
    }

    [Fact]
    public void Generate_ReportsMissionScriptingNotEnabled_InsteadOfFailingSilently()
    {
        string run = FunctionBody("local function mcpBridgeRunInMission(code)");
        Assert.Contains("MISSION_SCRIPTING_DISABLED", run);
        Assert.Contains("\"no mission is running\"", run);
        Assert.Contains("success == false", run); // dostring_in's second return value flags a script error
    }

    [Fact]
    public void Generate_EmbedsTheLinkSecret_AndChecksTheAuthLine()
    {
        Assert.Contains("local LINK_SECRET = \"TestLinkSecret_0123456789\"", Lua);
        Assert.Contains("\"AUTH \" .. LINK_SECRET", Lua);
        Assert.Contains("{\"authOk\":true,", Lua); // plus the protocol version, see DcsWireContractTests
        Assert.Contains("{\"authError\":true}", Lua);
    }

    [Fact]
    public void Generate_DropsClientsThatDontAuthenticateInTime()
    {
        // Otherwise any local process could connect first and squat the single client slot.
        Assert.Contains("AUTH_TIMEOUT = 2", Lua);
    }

    [Fact]
    public void Generate_RunsNoCommandsAndSendsNothing_BeforeAuthentication()
    {
        // Every outbound path (telemetry, heartbeat, pause, forwarded logs) goes through a guard,
        // and command execution only happens after the auth line has been accepted.
        Assert.Contains("if not McpBridge.authenticated then return end", FunctionBody("local function mcpBridgeSendLine"));
        string read = FunctionBody("local function mcpBridgeReadCommands");
        Assert.True(read.IndexOf("McpBridge.authenticated") < read.IndexOf("mcpBridgeHandleCommand(line)"),
            "the auth check must come before any command is handled");
    }

    [Theory]
    [InlineData("has\"quote")]
    [InlineData("has space")]
    [InlineData("")]
    public void Generate_RejectsSecretsThatCouldBreakOutOfTheLuaString(string secret) =>
        Assert.Throws<ArgumentException>(() => LuaHooksScriptGenerator.Generate("127.0.0.1", 1024, secret));

    [Fact]
    public void Generate_ReportsPauseAndResume()
    {
        Assert.Contains("{\"paused\":true}", FunctionBody("function mcpBridgeCallbacks.onSimulationPause"));
        Assert.Contains("{\"paused\":false}", FunctionBody("function mcpBridgeCallbacks.onSimulationResume"));
    }

    // From a function's declaration up to the next function declaration.
    private static string FunctionBody(string declaration)
    {
        int start = Lua.IndexOf(declaration);
        Assert.True(start >= 0, $"{declaration} not found");
        int next = Lua.IndexOf("function ", start + declaration.Length);
        return Lua[start..(next < 0 ? Lua.Length : next)];
    }

    [Fact]
    public void Generate_ReportsNoMissionOnSimulationStop()
    {
        string stop = Lua[Lua.IndexOf("onSimulationStop")..];
        Assert.Contains("{\"missionActive\":false}", stop);
    }
}
