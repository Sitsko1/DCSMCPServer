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
    and the DCS-side wire contract (below). `BridgeStatus` also carries `McpEndpoint`/
    `DcsEndpoint` (the actual configured listen URL / DCS address, set by `StartAsync`) so a UI
    never has to hardcode them — render from these, not literals.
  - `DcsMcpBridgeHost` registers `DcsConnection` as both a singleton (`IDcsConnection` resolves
    to it) and a hosted service — `DcsConnection` has no dependency back on `AtcTools`, so this
    one-directional wiring is safe. (A hand-rolled predecessor of this same pattern got the
    direction backwards — a tool handler that depended on the bridge, which the bridge's own DI
    factory then depended on — and the resulting circular resolution recursed with **zero
    output**, on stdout or stderr, looking exactly like a startup hang. If a new tool needs to
    depend on `DcsConnection`, keep the dependency one-directional.) Its constructor takes an
    optional `BridgeStatus` (defaults to `new()`) specifically so a caller that restarts the host
    (e.g. after a settings change) can pass the same instance through across `StopAsync`/
    `StartAsync` calls — a UI subscribed to `Changed` would otherwise go stale on every restart,
    since a fresh `DcsMcpBridgeHost` would mean a fresh `BridgeStatus` object.
  - `LuaExportScriptGenerator.cs` / `LuaExportDeployer.cs` — generate and deploy the DCS-side
    `Export.lua` companion script (see below). Both are pure/testable: the generator is a string
    builder, the deployer only does file I/O against a passed-in path (unit-tested against temp
    dirs).
- **`src/DCS.AIAutomator`** — WinUI 3 app, `net10.0-windows10.0.19041.0`, MSIX-packaged
  (`Platforms=x86;x64;ARM64`; build/run needs an explicit platform — see Commands). The actual
  entry point: `App.xaml.cs` creates a `DcsMcpBridgeHost`, starts it (with settings from
  `SettingsService`) on a background task, and passes `Status` to `MainWindow`, which subscribes
  to `BridgeStatus.Changed` and re-renders on the UI thread via `DispatcherQueue`. `App` also
  owns the current `ElementTheme` and applies it to every open window's root (`MainWindow`,
  `SettingsWindow`) — a window created after a theme toggle wouldn't otherwise pick it up.
  `Window.Closed` (on `MainWindow`) disposes the host.
  - `MainWindow.xaml` — "glass cockpit" annunciator panel, deliberately not the translucent Mica
    default (the exact palette is the point, so it shouldn't passively drift with the desktop
    wallpaper): two annunciators (MCP Bridge, DCS Connection) reading like glareshield
    master-caution lights, plus a quiet mission readout below (aircraft / mission / terrain /
    mode, or "no active mission"). Labels use Segoe UI Variable; data values use a monospace
    stack (`Cascadia Mono, Consolas`) — prose vs. precise readout is the deliberate type
    pairing. Top-right toolbar is two chromeless "glass" icon buttons (`GlassIconButtonStyle`/
    `GlassIconGhostButtonStyle` in `ThemeResources.xaml`) — a settings gear (opens
    `SettingsWindow` via `App.OpenSettingsWindow()`) and the light/dark toggle (swaps a
    `FontIcon` glyph between sun/moon; see `App.SetAppTheme`).
  - `SettingsWindow.xaml`/`.xaml.cs` — MCP/DCS connection settings, DCS install/Saved Games
    paths (with `FolderPicker` browse buttons), and the Export.lua deploy button, grouped under
    a top `NavigationView` (`Connection` / `Paths` / `DCS Integration`). Nothing persists until
    **Save**, which writes through `SettingsService` and restarts `DcsMcpBridgeHost` (via
    `App.RestartBridgeAsync`) so port/host changes take effect immediately, without an app
    restart.
  - `SettingsService.cs` — reads/writes settings via `ApplicationData.Current.LocalSettings`
    (the packaged-app key/value store). **Only ever add `ApplicationData` usage here, never in
    `src/DcsMcpBridge`** — it throws when the calling process isn't a packaged app, and the
    integration tests run `DcsMcpBridgeHost` unpackaged. The library takes settings as plain
    method parameters instead; the app project owns where they're stored.
  - `Themes/ThemeResources.xaml` — brushes (via `ResourceDictionary.ThemeDictionaries`, keyed
    `Light`/`Dark`/`Default`), fonts, and the shared `TextBlock`/card styles
    (`EyebrowLabelStyle`, `FieldValueStyle`, `SettingsCardStyle`, `GlassIconButtonStyle`, etc.),
    merged into `App.xaml`'s `Application.Resources`. A brush that's only meaningful per-theme
    must be referenced via `{ThemeResource ...}`, not `{StaticResource ...}` — `StaticResource`
    can't see into `ThemeDictionaries` at all, so a theme-scoped key referenced that way silently
    fails to resolve.
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
"garbage line" (leave prior state alone).

`LuaExportScriptGenerator` now generates a real Export.lua companion script matching this exact
contract (deployed via `LuaExportDeployer`/`SettingsWindow`'s "Deploy Lua scripts" button — see
below). **The generated script's `missionActive`/`missionName`/`terrain`/`aircraft` fields are
grounded in `LoGetMissionInfo()`/`LoGetSelfData()` (real Export-environment calls), but
`multiplayer` is speculative** — it tries `net.get_server_id()`, a Hooks-environment API with no
confirmed Export-environment equivalent, and will likely just always report `false` until
verified against a live DCS session. If you touch the generator, keep
`DcsTelemetryMessage`/`DcsTelemetryParser` and the Lua's `string.format(...)` JSON in sync —
nothing enforces the contract across the language boundary.

### Export.lua deployment: DCS is the socket server, this app is the client

`DcsConnection` connects *out* to `dcsIp:dcsPort` (`TcpClient.ConnectAsync`) — so the DCS-side
script must be the one listening. DCS's Export hooks run every simulation frame and can't block
on `accept()`, but LuaSocket (bundled with DCS under `Scripts/LuaSocket`) supports exactly this
non-blocking listener pattern — it's the same shape as LuaSocket's own bundled
`Listener.lua`/`Talker.lua` examples. `LuaExportScriptGenerator` binds a non-blocking server
socket in `LuaExportStart`, accepts a pending connection (if any) once per frame in
`LuaExportAfterNextFrame`, drains and `loadstring()`-executes queued commands (what
`AtcTools`/`DcsConnection.SendLuaCommand` sends), then writes one telemetry JSON line. It
**chains** onto any pre-existing `LuaExportStart`/`LuaExportAfterNextFrame`/`LuaExportStop`
(saves the previous function, calls it first) instead of overwriting them — DCS-BIOS,
DCSFlightpanels, VAICOM, etc. commonly already define these same hooks in a user's Export.lua,
and clobbering them would break those tools. `LuaExportDeployer` mirrors this: it writes the
generated script to `Scripts/DCSMcpBridgeExport.lua` and only *appends* a guarded `dofile(...)`
line to `Scripts/Export.lua` if one isn't already present (backing up the original first);
re-running deploy is a no-op on `Export.lua` if already wired up.

None of this has been run against a live DCS instance — treat the socket-direction and hook-
chaining design as verified against LuaSocket/DCS documentation, not against a real session.

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
- **`ApplicationData` only works in a packaged process.** `SettingsService` (in
  `src/DCS.AIAutomator`) uses `ApplicationData.Current.LocalSettings`, which throws outside a
  packaged app. `test/DcsMcpBridge.IntegrationTests` runs `DcsMcpBridgeHost` unpackaged, so
  `ApplicationData` must never end up in `src/DcsMcpBridge` — settings flow into the library as
  plain method parameters (`StartAsync(listenUrl, dcsIp, dcsPort)`), never read from storage
  directly by library code.
- **`FolderPicker` needs HWND interop in a WinUI 3 desktop app**, or it throws with no window to
  anchor to: `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window))`
  before calling `PickSingleFolderAsync()` (see `SettingsWindow.xaml.cs`).
