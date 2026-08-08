# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

An MCP (Model Context Protocol) server that lets an LLM control DCS World (the flight
simulator) by talking to its `Export.lua` TCP telemetry/command port, plus a WinUI 3 desktop
app that hosts it and shows live status. `DCS.AIAutomator` is the application users actually
run; `DcsMcpBridge` is a library it (and tests) load in-process — it has no entry point of its
own. An earlier hand-rolled JSON-RPC-over-stdio implementation (`src/DcsMcpServer`,
`src/McpBridgeHostedService`) and an earlier stdio-exe version of `DcsMcpBridge` were both
deleted once superseded; don't recreate either pattern.

## Why HTTP transport, not stdio

MCP's stdio transport assumes a client spawns the server process per session and owns its
stdio for that session's lifetime. That's a bad fit for an app with a persistent status
window: the window would need to flash open/close per session, and `DCS.AIAutomator` is
MSIX-packaged, whose activation model doesn't reliably support the stdio redirection MCP
clients expect anyway. Instead `DcsMcpBridgeHost` runs the MCP server over HTTP
(`ModelContextProtocol.AspNetCore`, `WithHttpTransport`) as a background task inside the
WinUI app's process: the app is a long-running server, MCP clients connect to a local URL, and
the window's "bridge running" status is just the host's own lifetime state.

## Layout

- **`src/DcsMcpBridge`** — class library, `IsAotCompatible=true` (not `PublishAot` — that's a
  publish-time app property, meaningless on a library; this just keeps trim/reflection
  analysis warnings visible while writing the code).
  - `DcsMcpBridgeHost.cs` — the composition root any host calls: `StartAsync` builds a
    `WebApplication` (`AddMcpServer().WithHttpTransport(...).WithTools<AtcTools>(...)`,
    `MapMcp("/mcp")`), registers `DcsConnection`, and runs it; `Status` exposes the live
    `BridgeStatus` for a UI (or a test) to observe. `StopAsync`/`DisposeAsync` tear it down.
  - `Tools/AtcTools.cs` (`AtcTools`, `[McpServerToolType]`) — `send_atc_instruction`, declared
    with the SDK's attribute style (`[McpServerTool(Name = "...")]`, `[Description]` on the
    method and each parameter) instead of hand-built JSON schema.
  - `IDcsConnection.cs` / `DcsConnection.cs` — the DCS-specific piece the SDK has no equivalent
    for: `DcsConnection` is a `BackgroundService` that owns the persistent TCP connection to
    `Export.lua` (`127.0.0.1:1024`, 3s reconnect loop), implements `SendLuaCommand` via
    `IDcsConnection`, and parses each incoming line as telemetry (see below), updating the
    shared `BridgeStatus`. `AtcTools` depends on `IDcsConnection` (not the concrete class), so
    unit tests can substitute a fake instead of needing a real socket.
  - `BridgeStatus.cs` / `MissionInfo.cs` / `DcsTelemetryParser.cs` — the observable status model
    and the DCS-side wire contract (below).
  - `DcsMcpBridgeHost` registers `DcsConnection` as both a singleton (`IDcsConnection` resolves
    to it) and a hosted service — `DcsConnection` has no dependency back on `AtcTools`, so this
    one-directional wiring is safe. (A hand-rolled predecessor of this same pattern got the
    direction backwards — a tool handler that depended on the bridge, which the bridge's own DI
    factory then depended on — and the resulting circular resolution recursed with **zero
    output**, on stdout or stderr, looking exactly like a startup hang. If a new tool needs to
    depend on `DcsConnection`, keep the dependency one-directional.)
- **`src/DCS.AIAutomator`** — WinUI 3 app, `net10.0-windows10.0.19041.0`, MSIX-packaged
  (`Platforms=x86;x64;ARM64`; build/run needs an explicit platform — see Commands). The actual
  entry point: `App.xaml.cs` creates a `DcsMcpBridgeHost`, starts it on a background task, and
  passes `Status` to `MainWindow`, which subscribes to `BridgeStatus.Changed` and re-renders on
  the UI thread via `DispatcherQueue`. `Window.Closed` disposes the host.
  - `MainWindow.xaml` — "glass cockpit" annunciator panel, deliberately not the translucent Mica
    default (the exact palette is the point, so it shouldn't passively drift with the desktop
    wallpaper): two annunciators (MCP Bridge, DCS Connection) reading like glareshield
    master-caution lights, plus a quiet mission readout below (aircraft / mission / terrain /
    mode, or "no active mission"). Labels use Segoe UI Variable; data values use a monospace
    stack (`Cascadia Mono, Consolas`) — prose vs. precise readout is the deliberate type
    pairing.
  - `Themes/ThemeResources.xaml` — brushes (via `ResourceDictionary.ThemeDictionaries`, keyed
    `Light`/`Dark`/`Default`), fonts, and the shared `TextBlock` styles (`EyebrowLabelStyle`,
    `FieldValueStyle`, etc.), merged into `App.xaml`'s `Application.Resources`. A brush that's
    only meaningful per-theme must be referenced via `{ThemeResource ...}`, not
    `{StaticResource ...}` — `StaticResource` can't see into `ThemeDictionaries` at all, so a
    theme-scoped key referenced that way silently fails to resolve. The window has a runtime
    light/dark toggle (`ThemeToggle` in `MainWindow.xaml`, wired to
    `App.SetAppTheme(ElementTheme)`, which sets `RequestedTheme` on the window's root element) —
    it's the reason the theme split into per-mode dictionaries instead of one fixed dark
    palette.
- **`test/DcsMcpBridge.UnitTests`** — xUnit. `AtcToolsTests` against `FakeDcsConnection` (no real
  socket), `DcsTelemetryParserTests` against raw JSON-lines input.
- **`test/DcsMcpBridge.IntegrationTests`** — xUnit. `McpServerIntegrationTests` starts a real
  `DcsMcpBridgeHost` in-process on port 5271 (distinct from the app's default 5270, so a running
  dev instance of `DCS.AIAutomator` doesn't collide with the test run) and drives it with the
  SDK's own `McpClient`/`HttpClientTransport` — the same client-side API a real MCP client uses.
- **`DcsMcp.slnx`** at the repo root ties all four projects together, with the WinUI project's
  platform (`x64`) pinned in the solution file itself.

## DCS-side telemetry contract (assumed, not fixed)

`DcsTelemetryParser` expects one JSON object per line from Export.lua:

```json
{"missionActive": true, "missionName": "...", "terrain": "...", "aircraft": "...", "multiplayer": false}
```

`missionActive: false` (or omitted fields while active) is valid — `TryParse` returns `false`
only for genuinely malformed input, distinguishing "no mission" (clear `CurrentMission`) from
"garbage line" (leave prior state alone). **This shape doesn't come from a real Export.lua
script yet** — there isn't one in this repo. If you're adding one, either match this contract
or update `DcsTelemetryMessage`/`DcsTelemetryParser` to match whatever it actually sends.

## Commands

```bash
dotnet build DcsMcp.slnx     # build everything (WinUI platform is pinned in the .slnx)
dotnet test DcsMcp.slnx      # run unit + integration tests
```

To actually run the app, deploy it from Visual Studio and launch the installed package — not
`dotnet run`, which doesn't reliably work here (see Gotchas below).

Building/running `src/DCS.AIAutomator` directly (not via the `.slnx`) needs `-p:Platform=x64`
(or `x86`/`ARM64`) — it has no `AnyCPU` platform, and MSIX packaging fails without one.

To run a single test: `dotnet test test/DcsMcpBridge.UnitTests --filter FullyQualifiedName~AtcToolsTests`.

## Gotchas specific to this repo

- **Watch for WPF patterns leaking into `src/DCS.AIAutomator` — it's WinUI 3, not WPF, and the
  two APIs share names but aren't interchangeable.** This has already happened once (a VS-side
  edit added `using System.Windows;`, overrode `App.OnStartup(StartupEventArgs e)`, and called
  the unpackaged-only `DeploymentManager.Initialize()` bootstrap — none of which exist or apply
  on `Microsoft.UI.Xaml.Application`; the packaged app never needed manual runtime
  bootstrapping at all). If code here doesn't compile with a "type not found" on something that
  sounds like a normal XAML type, check whether it's actually a `System.Windows.*` (WPF) type
  instead of the `Microsoft.UI.Xaml.*` (WinUI) one. The other common mixed-up pair is XAML
  markup extensions: `{StaticResource}` cannot resolve a key that only exists inside
  `ResourceDictionary.ThemeDictionaries` — use `{ThemeResource}` for anything theme-scoped (see
  `Themes/ThemeResources.xaml` above). Neither mistake is a WPF-only failure mode, but WPF
  habits are exactly what reintroduces them here.
- **`dotnet run` doesn't reliably launch this app; deploy/run the installed package instead.**
  Both the raw `.exe` from `bin/` and `dotnet run --project src/DCS.AIAutomator` can throw
  `COMException 0x80040154 (REGDB_E_CLASSNOTREG)` from `DeploymentManagerCS.AutoInitialize`
  before `App.OnLaunched` ever runs — a Windows App Runtime registration gap that `dotnet run`'s
  lighter "loose-layout" dev path doesn't reliably paper over, even with the runtime installed.
  Deploying via Visual Studio (Package and Publish, or F5) registers the app as a real installed
  package and resolves it — confirmed working end to end afterward: launch via the installed
  package's AppsFolder entry (Start menu, or `explorer.exe "shell:appsFolder\<PackageFamilyName>!App"`
  — find the AUMID with `Get-StartApps | Where-Object Name -like '*AIAutomator*'`), not
  `dotnet run`, if you hit this again. Don't assume new code broke something before checking
  which launch path you used.
- **AOT/reflection:** the SDK's default (reflection-free) serializer options don't cover custom
  types. `AtcAction` needs its own `[JsonSerializable]`-annotated `JsonSerializerContext`
  (`AtcJsonContext` in `AtcTools.cs`) merged into a `JsonSerializerOptions.TypeInfoResolverChain`
  alongside `McpJsonUtilities.DefaultOptions.TypeInfoResolver`, passed to `.WithTools<T>(options)`
  in `DcsMcpBridgeHost.StartAsync` — otherwise `NotSupportedException: JsonTypeInfo metadata for
  type '...' was not provided` on first `tools/list`/`tools/call`. Enum parameters also need
  `[JsonConverter(typeof(JsonStringEnumConverter<TEnum>))]` directly on the enum, since the
  SDK's default string-enum converter registration is itself gated on reflection being enabled.
  Same applies to any new telemetry DTO parsed with `System.Text.Json` — see
  `DcsTelemetryJsonContext` in `DcsTelemetryParser.cs` for the pattern.
- **ASP.NET Core APIs from a plain library project:** `src/DcsMcpBridge` uses
  `Microsoft.NET.Sdk` (not `Sdk.Web`), so `WebApplication`/Kestrel/`MapMcp` need an explicit
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in the csproj — without it,
  `WebApplication.CreateBuilder` doesn't resolve.
- **Responses can arrive out of order:** the SDK may handle concurrent requests out of
  submission order — don't assume request N's response is read N-th.
- **Logging:** `DcsMcpBridgeHost` clears default logging providers and adds only `AddDebug()`.
  A WinUI GUI-subsystem app has no console attached, so the usual `AddConsole()` either does
  nothing useful or risks throwing on an invalid handle — don't add it back without checking.
