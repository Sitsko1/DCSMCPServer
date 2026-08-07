# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

An MCP (Model Context Protocol) server that lets an LLM control DCS World (the flight
simulator) by talking to its `Export.lua` TCP telemetry/command port. One project, built on
the official `ModelContextProtocol` NuGet SDK — an earlier hand-rolled JSON-RPC-over-stdio
implementation (`src/DcsMcpServer`, `src/McpBridgeHostedService`) was deleted once this one
reached parity; don't recreate that pattern.

## Layout

- **`src/DcsMcpBridge`** — the server. Generated from the official `dotnet new mcpserver`
  template; self-contained/AOT-publishable (`PublishAot`, `PublishSingleFile`,
  `RuntimeIdentifiers`). `Program.cs` wires it up via
  `AddMcpServer().WithStdioServerTransport().WithTools<T>()`.
  - `Tools/RandomNumberTools.cs` — the original template sample (`get_random_number`).
  - `Tools/AtcTools.cs` (`AtcTools`, `[McpServerToolType]`) — `send_atc_instruction`, declared
    with the SDK's attribute style (`[McpServerTool(Name = "...")]`, `[Description]` on the
    method and each parameter) instead of hand-built JSON schema.
  - `IDcsConnection.cs` / `DcsConnection.cs` — the DCS-specific piece the SDK has no equivalent
    for: `DcsConnection` is a `BackgroundService` that owns the persistent TCP connection to
    `Export.lua` (`127.0.0.1:1024`, 3s reconnect loop) and implements `SendLuaCommand` via
    `IDcsConnection`. `AtcTools` depends on the `IDcsConnection` interface (not the concrete
    class), so unit tests can substitute a fake instead of needing a real socket.
  - `Program.cs` registers `DcsConnection` as both a singleton (`IDcsConnection` resolves to
    it) and a hosted service (`AddHostedService(sp => sp.GetRequiredService<DcsConnection>())`)
    — `DcsConnection` has no dependency back on `AtcTools`, so this one-directional wiring is
    safe. (A hand-rolled predecessor of this same pattern got the direction backwards — a tool
    handler that depended on the bridge, which the bridge's own DI factory then depended on —
    and the resulting circular resolution recursed with **zero output**, on stdout or stderr,
    looking exactly like a startup hang. If a new tool needs to depend on `DcsConnection` (or
    anything else registered as a hosted service), keep the dependency one-directional.)
- **`test/DcsMcpBridge.UnitTests`** — xUnit, references `src/DcsMcpBridge` directly. Tests
  `AtcTools` against `FakeDcsConnection` (records the last Lua string sent, no real socket) and
  `RandomNumberTools`' bounds.
- **`test/DcsMcpBridge.IntegrationTests`** — xUnit, one test
  (`McpServerIntegrationTests`) that spawns the real built server as a subprocess and drives it
  over stdio exactly like an MCP client: sends `initialize`/`tools/list`/`tools/call`, asserts
  on the raw JSON-RPC response lines. See gotchas below — both were found by this test (or its
  manual precursor) failing.
- **`DcsMcp.slnx`** at the repo root ties all three projects together.

## Commands

```bash
dotnet build DcsMcp.slnx     # build everything
dotnet test DcsMcp.slnx      # run unit + integration tests
dotnet run --project src/DcsMcpBridge   # run the server over stdio (for a real MCP client)
```

To run a single test: `dotnet test test/DcsMcpBridge.UnitTests --filter FullyQualifiedName~AtcToolsTests`.

## Gotchas specific to this repo

- **AOT/reflection:** `PublishAot=true` on `src/DcsMcpBridge` disables reflection-based
  `System.Text.Json` *even in a plain Debug build*, not just on publish. Any custom type used
  as a tool parameter needs its own `[JsonSerializable]`-annotated `JsonSerializerContext`
  (see `AtcJsonContext` in `AtcTools.cs`) merged into a `JsonSerializerOptions.TypeInfoResolverChain`
  alongside `McpJsonUtilities.DefaultOptions.TypeInfoResolver`, passed to `.WithTools<T>(options)`
  in `Program.cs` — otherwise `NotSupportedException: JsonTypeInfo metadata for type '...' was
  not provided` at first `tools/list`/`tools/call`. Enum parameters also need
  `[JsonConverter(typeof(JsonStringEnumConverter<TEnum>))]` directly on the enum, since the
  SDK's default string-enum converter registration is itself gated on reflection being enabled.
- **Self-contained build output isn't where a naive `ProjectReference` copy expects:** because
  `SelfContained=true`, the real runnable output (with `hostpolicy.dll` etc.) lives at
  `src/DcsMcpBridge/bin/<Config>/net10.0/<RID>/`. A project that merely references
  `DcsMcpBridge.csproj` gets a thin framework-dependent copy in its own output directory that
  fails with "hostpolicy.dll ... was not found". `McpServerIntegrationTests.FindServerExePath()`
  resolves the real path and launches the self-contained `.exe` directly (no `dotnet` muxer).
- **Responses can arrive out of order:** the SDK may handle concurrent requests out of
  submission order, so a test (or client) that fires several requests before reading responses
  must match each response by its `"id"`, not by read order.
- **stdout is reserved for JSON-RPC frames**; all logging goes to stderr
  (`builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`). Never
  write to stdout outside the SDK's own transport.
- **Manual stdin testing:** piping a finite file into the app's stdin (`< input.jsonl`) can hit
  EOF and trigger transport shutdown *before* the async response pipeline flushes replies still
  in flight — stdout looks empty even though stderr shows every handler completing. Keep stdin
  open a beat longer, e.g. `(cat input.jsonl; sleep 5) | dotnet ...`. A real MCP client doesn't
  close stdin between messages, so this only bites manual/ad-hoc testing (the integration test
  avoids it by never closing stdin until the process is killed).
