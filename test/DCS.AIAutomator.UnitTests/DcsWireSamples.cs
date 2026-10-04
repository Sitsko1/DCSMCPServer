using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// One sample line per message type of the DCS wire contract (Hooks script → app). The parser
/// tests parse these, and the contract tests below check that the field names in them, in the
/// generated Lua and in the C# DTOs are the same set, so a field renamed or added on one side
/// only fails here instead of going silently missing. When this fails on purpose, update the
/// samples and bump <see cref="LuaHooksScriptGenerator.ProtocolVersion"/>.
/// </summary>
public static class DcsWireSamples
{
    public const string MissionWithOwnship = """{"missionActive":true,"missionName":"Quick Start","terrain":"Caucasus","aircraft":"Su-27","ownship":{"lat":41.7,"lon":41.7,"altMsl":2000.0,"altAgl":1800.0,"ias":160.0,"tas":180.0,"mach":0.54,"vs":0.0,"hdg":6.17,"failures":["GearFailure"]}}""";
    public const string MissionWithOwnshipNoFailures = """{"missionActive":true,"missionName":"Quick Start","terrain":"Caucasus","aircraft":"Su-27","ownship":{"lat":41.7,"lon":41.7,"altMsl":2000.0,"altAgl":1800.0,"ias":160.0,"tas":180.0,"mach":0.54,"vs":0.0,"hdg":6.17,"failures":[]}}""";
    public const string MissionWithOwnshipFailuresNotReported = """{"missionActive":true,"missionName":"Quick Start","terrain":"Caucasus","aircraft":"F/A-18C","ownship":{"lat":41.7,"lon":41.7,"altMsl":null,"altAgl":null,"ias":160.0,"tas":180.0,"mach":0.54,"vs":0.0,"hdg":6.17,"failures":null}}""";
    public const string MissionWithoutOwnship = """{"missionActive":true,"missionName":"Quick Start","terrain":"Caucasus","aircraft":"Unknown"}""";
    public const string MissionEnded = """{"missionActive":false}""";
    public const string Heartbeat = """{"heartbeat":true}""";
    public const string Paused = """{"paused":true}""";
    public const string Resumed = """{"paused":false}""";
    public const string Log = """{"log":{"level":"error","message":"frame error: boom"}}""";
    public static readonly string AuthOk = $$"""{"authOk":true,"protocol":{{LuaHooksScriptGenerator.ProtocolVersion}}}""";
    public const string AuthError = """{"authError":true}""";
    public const string CommandOk = """{"commandResult":{"id":7,"ok":true}}""";
    public const string CommandWithData = """{"commandResult":{"id":9,"ok":true,"data":[{"group":"Enfield-1","callsign":"Enfield11","type":"F/A-18C","coalition":2,"lat":41.7,"lon":41.7,"altMsl":4572.0,"player":false}]}}""";
    public const string CommandFailed = """{"commandResult":{"id":8,"ok":false,"error":"no mission is running"}}""";

    public static IEnumerable<string> All =>
    [
        MissionWithOwnship, MissionWithOwnshipNoFailures, MissionWithOwnshipFailuresNotReported,
        MissionWithoutOwnship, MissionEnded, Heartbeat, Paused, Resumed, Log, AuthOk, AuthError,
        CommandOk, CommandWithData, CommandFailed,
    ];

    public static TheoryData<string> AllLines => new(All);
}

public class DcsWireContractTests
{
    private static readonly string Lua = LuaHooksScriptGenerator.Generate("127.0.0.1", 1024, "TestLinkSecret_0123456789");

    [Theory]
    [MemberData(nameof(DcsWireSamples.AllLines), MemberType = typeof(DcsWireSamples))]
    public void EverySample_Parses(string line) =>
        Assert.True(DcsTelemetryParser.TryParse(line, out _));

    [Fact]
    public void TheGeneratedLua_WritesExactlyTheSampleFields() =>
        AssertSameKeys(LuaKeys(), "generated Lua");

    [Fact]
    public void TheCSharpDtos_ReadExactlyTheSampleFields() =>
        AssertSameKeys(DtoKeys(), "C# DTOs");

    // Names the odd fields out; a plain set comparison would only say "sets differ".
    private static void AssertSameKeys(SortedSet<string> actual, string side)
    {
        SortedSet<string> samples = SampleKeys();
        Assert.Empty(actual.Except(samples).Select(k => $"{k}: in the {side}, not in DcsWireSamples")
            .Concat(samples.Except(actual).Select(k => $"{k}: in DcsWireSamples, not in the {side}")));
    }

    [Fact]
    public void TheGeneratedLua_ReportsTheAppsProtocolVersion()
    {
        Assert.Contains($"local PROTOCOL_VERSION = {LuaHooksScriptGenerator.ProtocolVersion}", Lua);
        Assert.Contains("""'{"authOk":true,"protocol":' .. PROTOCOL_VERSION .. '}'""", Lua);
    }

    // Every JSON key the Lua writes: a double-quoted name right after "{" or "," (so a quoted
    // word in a Lua comment doesn't count).
    private static SortedSet<string> LuaKeys() =>
        new(Regex.Matches(Lua, "[{,]\"(\\w+)\":").Select(m => m.Groups[1].Value));

    private static SortedSet<string> SampleKeys()
    {
        var keys = new SortedSet<string>();
        foreach (string line in DcsWireSamples.All) Collect(JsonDocument.Parse(line).RootElement, keys);
        return keys;

        static void Collect(JsonElement e, SortedSet<string> keys)
        {
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in e.EnumerateArray()) Collect(item, keys);
            }
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty p in e.EnumerateObject())
            {
                keys.Add(p.Name);
                Collect(p.Value, keys);
            }
        }
    }

    private static SortedSet<string> DtoKeys() =>
        new(new[] { typeof(DcsTelemetryMessage), typeof(OwnshipTelemetry), typeof(DcsLogTelemetry), typeof(DcsCommandResultTelemetry), typeof(AiFlightTelemetry) }
            .SelectMany(t => t.GetProperties())
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name));
}
