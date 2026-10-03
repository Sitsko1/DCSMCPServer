# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

An MCP (Model Context Protocol) server that lets an LLM control DCS World (the flight
simulator) through a TCP telemetry/command socket opened by a Lua script it deploys into DCS, plus a WinUI 3 desktop
app that hosts it and shows live status. `DCS.AIAutomator` is the application users actually
run; `DcsMcpBridge` and `DCS.Scripting` are libraries it (and tests) load in-process — neither
has an entry point of its own.

**HTTP transport, not stdio — don't reintroduce stdio.** Stdio assumes the client spawns the
server per session, which fights a persistent status window, and MSIX activation doesn't
reliably support stdio redirection anyway. `DcsMcpBridgeHost` runs the server over HTTP
(`ModelContextProtocol.AspNetCore`) inside the app process; the window's "bridge running"
status is just the host's lifetime state. Earlier stdio implementations (`src/DcsMcpServer`,
`src/McpBridgeHostedService`, a stdio-exe `DcsMcpBridge`) were deleted on purpose.

**Single-player only until v2.** Multiplayer capabilities are deferred to version 2 or later
(maintainer decision) — don't build, test, or spec multiplayer behavior before then.

## Knowledge graph

`graphify-out/` (gitignored) holds a prebuilt knowledge graph of this repo. For
architecture / "how does X connect to Y" questions, query it first with
`/graphify query "<question>"` (read `graphify-out/GRAPH_REPORT.md` for the overview) before
grepping file by file. After significant code changes, refresh it with `/graphify . --update`.

## Layout

- **`src/DCS.Scripting`** — class library (`IsAotCompatible=true`), everything DCS-specific.
  Split from `DcsMcpBridge` so the app can call `LuaScriptDeployer` without depending on the
  MCP host.
  - `AddDcsScripting(status, dcsIp, dcsPort)` — the DI wiring: shared `BridgeStatus`
    singleton, `DcsConnection` as both `IDcsConnection` singleton and hosted service.
  - `AtcTools` — MCP tool `send_atc_instruction`, declared with SDK attributes
    (`[McpServerTool]`, `[Description]`), not hand-built JSON schema. Depends on
    `IDcsConnection` so tests use `FakeDcsConnection`.
  - `AircraftTools` — MCP tool `get_aircraft_state`; reads the latest snapshot from
    `BridgeStatus` only (never the connection). Values are formatted by
    `AircraftStateFormatter`/`UnitConversion`, the **single** formatting path shared with the
    main window's aircraft panel — don't format units anywhere else, or the two will drift.
  - `DcsConnection` — `BackgroundService` owning the TCP connection to the DCS script (3s
    reconnect loop); sends Lua commands and parses each incoming line as telemetry into
    `BridgeStatus`. **Keep dependencies one-directional (tool → connection, never back).**
    A predecessor had a tool ↔ bridge cycle, and the circular DI resolution recursed with
    zero output on stdout/stderr — indistinguishable from a startup hang.
  - `BridgeStatus` — observable status model. Its constructor takes no dependencies so the
    same instance survives host restarts (settings change → `StopAsync`/`StartAsync`);
    otherwise a UI subscribed to `Changed` goes stale. It also carries `McpEndpoint`/
    `DcsEndpoint` — render those in UI, never hardcoded literals.
  - `LuaHooksScriptGenerator` / `LuaScriptDeployer` — generate and deploy the DCS-side
    script (below). Pure string building / file I/O against a passed-in path.
- **`src/DcsMcpBridge`** — class library, just the MCP composition root: `DcsMcpBridgeHost`
  builds a `WebApplication` (`AddDcsScripting`, `WithHttpTransport`, `WithTools<AtcTools>`,
  `MapMcp("/mcp")`) and exposes `Status`. Owns no DCS logic. Also holds `DcsLogging` (the
  Serilog pipeline — see Gotchas) and the MCP tool-call logging filter.
- **`src/DCS.AIAutomator`** — WinUI 3 app, MSIX-packaged. `App.xaml.cs` creates the
  `DcsMcpBridgeHost` and `NotificationService`, starts the bridge on a background task with
  `SettingsService` values, and owns the current `ElementTheme` (applied to every open
  window, since a window created later wouldn't pick it up otherwise).
  - `NotificationService` — app-wide hub (`Show` → `History` + `Raised`); marshals to the
    captured `DispatcherQueue` because background services call it off the UI thread.
  - `BridgeStatusNotifier` — the one place that diffs `BridgeStatus.Changed` into
    transition notifications (bridge/DCS/mission). Don't duplicate this diffing elsewhere.
  - `ToastHost` — renders notifications as cards in code-behind; the severity accent brush is
    read from the active `ThemeDictionaries` entry at runtime (no `{ThemeResource}` markup
    available in code-behind).
  - `MainWindow` — "glass cockpit" annunciator panel with a fixed palette, deliberately not
    Mica. Below the mission readout, an "Aircraft status" `Expander` renders
    `BridgeStatus.Aircraft` (~5 Hz) in `BridgeStatus.Units`.
  - `SettingsWindow` — nothing persists until **Save**, which writes via `SettingsService`
    and calls `App.ApplySettingsAsync(restartBridge)`. The bridge (and so the DCS connection)
    restarts only when the MCP port or DCS host/port changed; everything else, including
    units (held on the shared `BridgeStatus`), applies without a restart.
  - `Themes/ThemeResources.xaml` — brushes (per-theme `ThemeDictionaries`), fonts, shared
    styles; merged into `App.xaml`.
- **`test/DcsMcpBridge.UnitTests`** — xUnit; tools against `FakeDcsConnection`, parser against
  raw JSON lines, generator/deployer against generated Lua / temp dirs.
- **`test/DcsMcpBridge.IntegrationTests`** — starts a real `DcsMcpBridgeHost` in-process on
  port **5271** (app default is 5270, so a running dev instance doesn't collide) and drives it
  with the SDK's `McpClient`/`HttpClientTransport`.

## DCS-side telemetry contract (assumed, not fixed)

`DcsTelemetryParser` expects one JSON object per line from the DCS script:

```json
{"missionActive": true, "missionName": "...", "terrain": "PersianGulf", "aircraft": "F/A-18C",
 "ownship": {"lat": 41.5, "lon": 41.7, "altMsl": 3000.0, "altAgl": 2950.0, "ias": 150.0,
             "tas": 160.0, "mach": 0.48, "vs": -5.5, "hdg": 1.5708, "failures": ["GearFailure"]}}
```

`ownship` is sent only when there's a player aircraft, in DCS's SI units (m, m/s, rad) —
conversion is C#-side only. Any value may be `null`; `failures: null` means this aircraft
doesn't report failures ("Not reported by this aircraft"), `[]` means none active. Failures come
from `LoGetMCPState`, which only the simplified **FC3** aircraft set — full-fidelity modules don't
(verified live on the F/A-18C), so the script sends `null` for any type not in its FC3 list. Telemetry is throttled to ~5 Hz on model time
(`TelemetryIntervalSeconds`), but commands are drained every frame.

Besides mission reports, the script sends `{"heartbeat":true}` (~1/s on `Sim.getRealTime()`,
independent of the telemetry throttle), `{"paused":true|false}` (from
`onSimulationPause`/`onSimulationResume`) and `{"log":{"level":"info|error","message":"..."}}`
(its own `mcpBridgeLog` output; also written to `dcs.log`). **Only lines carrying `missionActive` are mission
reports** — `DcsTelemetryMessage.MissionActive` is `bool?` on purpose, because reading an
absent field as `false` would clear the mission on every heartbeat. `TryParse` returns a
`DcsLine` saying which kind it was; any new message type must stay a non-mission-report too.

`DcsConnection`'s watchdog sets `BridgeStatus.DcsNotResponding` when a connected, mid-mission,
unpaused DCS sends nothing for `DefaultNotRespondingTimeout` (5 s) — a hung DCS, which unlike a
killed one keeps its socket open. It never closes the socket (maintainer decision: keep
waiting); any line clears it. A freeze in the DCS menus can't be detected: no Hooks callback
runs there. Verified live: normal pause (Esc) shows PAUSED; killing DCS shows DISCONNECTED and a
restarted DCS reconnects. **Active Pause is not reported** — DCS fires no
`onSimulationPause` for it (the rest of the sim keeps running), so the app stays CONNECTED; that's
a DCS limitation, not a bug. Suspending `DCS.exe` (Resource Monitor) shows NOT RESPONDING, and
resuming it returns to CONNECTED. Unverified live: whether `onSimulationFrame` keeps firing while
paused (the `paused` line makes it not matter).

`TryParse` returns `false` only for malformed input: `missionActive: false` clears
`CurrentMission`, a garbage line leaves prior state alone; missing fields default to
"Unknown", and unknown fields are ignored. `terrain` arrives as DCS's internal theatre ID and
the parser maps the non-obvious ones to their product names (`Falklands` → "South Atlantic").
The generator's Lua `string.format(...)` JSON and `DcsTelemetryMessage`/`DcsTelemetryParser`
must be kept in sync by hand — nothing enforces it across the language boundary.

**Check every DCS API call against the stock docs before using it** —
`DCS World/API/Sim_ControlAPI.md` (Hooks: `Sim.*`, callbacks, which `Export.Lo*` calls work
there) and `DCS World/Scripts/Export.lua` (the `Lo*` list). The script once relied on
`LoGetMissionInfo`, which doesn't exist; `pcall` swallowed the error and the app showed "No
active mission" forever. A speculative `multiplayer` field was removed (multiplayer is deferred
to v2).

### DCS-side script: a Hooks script, and DCS is the socket server

The DCS side is a **Hooks script** (`Saved Games\DCS\Scripts\Hooks\DCSMcpBridgeHooks.lua`), not
an `Export.lua` companion. Only the Hooks (GUI) environment has `Sim.*` — mission name
(`Sim.getMissionName()`), map (`Sim.getCurrentMission().mission.theatre`), and DCS's own
aircraft display names (`Sim.getUnitTypeAttribute(type, "DisplayName")`) — while still exposing
every Export call as `Export.Lo*`. DCS loads `Scripts/Hooks/*.lua` itself **at startup**
(restart DCS after deploying), so nothing edits the user's `Export.lua` and there's no hook
chaining with DCS-BIOS/Tacview/etc. `local Sim = Sim or DCS` covers older DCS versions.

`DcsConnection` connects *out*, so the script listens: a non-blocking LuaSocket server, polled
from `onSimulationFrame` (accept, `loadstring()`-execute queued commands, write one telemetry
line). `onSimulationStart` caches mission name/map; `onSimulationStop` sends
`missionActive:false`. The socket stays open between missions since the script lives as long
as DCS. Commands therefore run in the **GUI** Lua state — not the Export or mission-scripting
state.

`LuaScriptDeployer` writes the Hooks script, and migrates installs from earlier versions: it
removes our `dofile(...)` lines from `Export.lua` (other tools' lines untouched, `.bak` kept)
and deletes the old `DCSMcpBridgeExport.lua` scripts — left in place they'd bind the same port.
Idempotent: nothing to migrate + identical script → "Already deployed", writes nothing.

Verified against a live DCS session (single-player, F/A-18C quick-start): the Hooks script
deploys, DCS listens, the app connects, and the readout shows the correct mission name, map
and aircraft display name. Still unverified live: `send_atc_instruction` via a real MCP client
(and it's known broken — see issue #4).

The deployer only accepts a real Saved Games folder (`DcsPathValidator`: must contain `Config`,
must not contain `bin\DCS.exe`/`bin-mt\DCS.exe`). The install folder also has `Config` and a
`Scripts` folder, so it was easy to pick by mistake. The default path comes from the Windows
Saved Games known folder, not `%USERPROFILE%`, because users relocate it (e.g. `E:\SavedGames`).

## Commands

```bash
dotnet build DcsMcp.slnx     # build everything (WinUI platform x64 is pinned in the .slnx)
dotnet test DcsMcp.slnx      # unit + integration tests
dotnet test test/DcsMcpBridge.UnitTests --filter FullyQualifiedName~AtcToolsTests   # single test class
```

Building `src/DCS.AIAutomator` directly (not via the `.slnx`) needs `-p:Platform=x64` (or
`x86`/`ARM64`) — there's no `AnyCPU`, and MSIX packaging fails without one.

NuGet versions are centrally managed (`Directory.Packages.props`,
`ManagePackageVersionsCentrally=true`). Put the version in a `<PackageVersion>` there, and
use a version-less `<PackageReference>` in the csproj. A `Version=` on a csproj reference
fails the build.

## Gotchas

- **Running the app: deploy from Visual Studio (F5 or Package and Publish), then launch the
  installed package — not `dotnet run` or the raw `bin/` exe.** Those can throw
  `COMException 0x80040154 (REGDB_E_CLASSNOTREG)` from `DeploymentManagerCS.AutoInitialize`
  before `App.OnLaunched` runs. Launch via Start menu or
  `explorer.exe "shell:appsFolder\<PackageFamilyName>!App"` (AUMID:
  `Get-StartApps | Where-Object Name -like '*AIAutomator*'`). Check the launch path before
  assuming new code broke something.
- **WinUI 3, not WPF.** A VS-side edit once added `using System.Windows;`,
  `App.OnStartup(StartupEventArgs)` and `DeploymentManager.Initialize()` — none apply to
  `Microsoft.UI.Xaml.Application` (the packaged app needs no manual bootstrap). A "type not
  found" on a normal-sounding XAML type usually means a `System.Windows.*` type slipped in.
- **`{ThemeResource}`, not `{StaticResource}`, for theme-scoped keys** — `StaticResource`
  can't see into `ThemeDictionaries` and silently fails to resolve.
- **`ApplicationData` only works in a packaged process.** Keep it inside `SettingsService`
  only — never in `src/DcsMcpBridge` or `src/DCS.Scripting`, since integration tests run the
  host unpackaged. Libraries take settings as plain parameters
  (`StartAsync(listenUrl, dcsIp, dcsPort)`).
- **AOT/reflection:** the SDK's reflection-free serializer doesn't cover custom types. Tool
  parameter types need a `[JsonSerializable]` context (`AtcJsonContext` in `AtcTools.cs`)
  chained into `TypeInfoResolverChain` with `McpJsonUtilities.DefaultOptions.TypeInfoResolver`
  and passed to `.WithTools<T>(options)`, else `NotSupportedException: JsonTypeInfo metadata
  ... was not provided` on first `tools/list`. Enums need
  `[JsonConverter(typeof(JsonStringEnumConverter<TEnum>))]` on the enum itself. New telemetry
  DTOs follow `DcsTelemetryJsonContext` in `DcsTelemetryParser.cs`.
- **ASP.NET Core from plain libraries:** both libraries use `Microsoft.NET.Sdk`, so they need
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for `WebApplication`/`MapMcp`.
- **Responses can arrive out of order** — don't assume request N's response is read N-th.
- **Logging:** Serilog, configured in code (never `Serilog.Settings.Configuration` — reflection
  fights AOT/trimming) by `DcsLogging` in `src/DcsMcpBridge`: CLEF JSON lines, rolled daily into
  `dcs-aiautomator-yyyyMMdd.clef`, deleted after `LogRetentionDays` (default 7, by age via
  `retainedFileTimeLimit`). The app resolves the folder (`LocalCacheFolder\Logs`) and passes
  `DcsLogging.Provider` to `DcsMcpBridgeHost.StartAsync(loggerProvider:)` and uses it for its own
  loggers, so one file has everything. `DCS.Scripting` sees only `Microsoft.Extensions.Logging`.
  Without a provider (tests) the host falls back to `AddDebug()`. Never add `AddConsole()` — a
  GUI-subsystem app has no console. Use structured templates (`"{Tool}"`), not interpolation.
  **Never log secrets, tool arguments, or Lua command bodies** — the tool-call filter logs only
  name/outcome/duration, and an integration test asserts arguments never reach the log. DCS
  Hooks-script errors are forwarded as `{"log":{...}}` lines (rate-limited per message in Lua),
  logged under the `DCS` category, and raised as `BridgeStatus.DcsScriptError` for a toast.
- **`FolderPicker` needs HWND interop**:
  `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window))` before
  `PickSingleFolderAsync()` (see `SettingsWindow.xaml.cs`).
