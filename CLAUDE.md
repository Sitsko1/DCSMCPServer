# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

An MCP (Model Context Protocol) server that lets an LLM control DCS World (the flight
simulator) by talking to its `Export.lua` TCP telemetry/command port, plus a WinUI 3 desktop
app that hosts it and shows live status. `DCS.AIAutomator` is the application users actually
run; `DcsMcpBridge` and `DCS.Scripting` are libraries it (and tests) load in-process — neither
has an entry point of its own.

**HTTP transport, not stdio — don't reintroduce stdio.** Stdio assumes the client spawns the
server per session, which fights a persistent status window, and MSIX activation doesn't
reliably support stdio redirection anyway. `DcsMcpBridgeHost` runs the server over HTTP
(`ModelContextProtocol.AspNetCore`) inside the app process; the window's "bridge running"
status is just the host's lifetime state. Earlier stdio implementations (`src/DcsMcpServer`,
`src/McpBridgeHostedService`, a stdio-exe `DcsMcpBridge`) were deleted on purpose.

## Knowledge graph

`graphify-out/` (gitignored) holds a prebuilt knowledge graph of this repo. For
architecture / "how does X connect to Y" questions, query it first with
`/graphify query "<question>"` (read `graphify-out/GRAPH_REPORT.md` for the overview) before
grepping file by file. After significant code changes, refresh it with `/graphify . --update`.

## Layout

- **`src/DCS.Scripting`** — class library (`IsAotCompatible=true`), everything DCS-specific.
  Split from `DcsMcpBridge` so the app can call `LuaExportDeployer` without depending on the
  MCP host.
  - `AddDcsScripting(status, dcsIp, dcsPort)` — the DI wiring: shared `BridgeStatus`
    singleton, `DcsConnection` as both `IDcsConnection` singleton and hosted service.
  - `AtcTools` — MCP tool `send_atc_instruction`, declared with SDK attributes
    (`[McpServerTool]`, `[Description]`), not hand-built JSON schema. Depends on
    `IDcsConnection` so tests use `FakeDcsConnection`.
  - `DcsConnection` — `BackgroundService` owning the TCP connection to Export.lua (3s
    reconnect loop); sends Lua commands and parses each incoming line as telemetry into
    `BridgeStatus`. **Keep dependencies one-directional (tool → connection, never back).**
    A predecessor had a tool ↔ bridge cycle, and the circular DI resolution recursed with
    zero output on stdout/stderr — indistinguishable from a startup hang.
  - `BridgeStatus` — observable status model. Its constructor takes no dependencies so the
    same instance survives host restarts (settings change → `StopAsync`/`StartAsync`);
    otherwise a UI subscribed to `Changed` goes stale. It also carries `McpEndpoint`/
    `DcsEndpoint` — render those in UI, never hardcoded literals.
  - `LuaExportScriptGenerator` / `LuaExportDeployer` — generate and deploy the DCS-side
    script (below). Pure string building / file I/O against a passed-in path.
- **`src/DcsMcpBridge`** — class library, just the MCP composition root: `DcsMcpBridgeHost`
  builds a `WebApplication` (`AddDcsScripting`, `WithHttpTransport`, `WithTools<AtcTools>`,
  `MapMcp("/mcp")`) and exposes `Status`. Owns no DCS logic.
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
    Mica.
  - `SettingsWindow` — nothing persists until **Save**, which writes via `SettingsService`
    and calls `App.RestartBridgeAsync` so port/host changes apply without an app restart.
  - `Themes/ThemeResources.xaml` — brushes (per-theme `ThemeDictionaries`), fonts, shared
    styles; merged into `App.xaml`.
- **`test/DcsMcpBridge.UnitTests`** — xUnit; tools against `FakeDcsConnection`, parser against
  raw JSON lines, generator/deployer against generated Lua / temp dirs.
- **`test/DcsMcpBridge.IntegrationTests`** — starts a real `DcsMcpBridgeHost` in-process on
  port **5271** (app default is 5270, so a running dev instance doesn't collide) and drives it
  with the SDK's `McpClient`/`HttpClientTransport`.

## DCS-side telemetry contract (assumed, not fixed)

`DcsTelemetryParser` expects one JSON object per line from Export.lua:

```json
{"missionActive": true, "missionName": "...", "terrain": "...", "aircraft": "...", "multiplayer": false}
```

`TryParse` returns `false` only for malformed input: `missionActive: false` clears
`CurrentMission`, a garbage line leaves prior state alone. The generator's Lua
`string.format(...)` JSON and `DcsTelemetryMessage`/`DcsTelemetryParser` must be kept in sync
by hand — nothing enforces it across the language boundary. `missionActive`/`missionName`/
`terrain`/`aircraft` come from real Export APIs (`LoGetMissionInfo`/`LoGetSelfData`);
`multiplayer` is speculative (`net.get_server_id()` is a Hooks-environment API) and will
likely always report `false`.

### Export.lua deployment: DCS is the socket server, this app is the client

`DcsConnection` connects *out*, so the Lua side listens: a non-blocking LuaSocket server bound
in `LuaExportStart`, accepting/draining once per frame in `LuaExportAfterNextFrame`
(`loadstring()`-executing received commands, then writing one telemetry line). It **chains**
onto any existing `LuaExportStart`/`AfterNextFrame`/`Stop` instead of overwriting them —
DCS-BIOS, DCSFlightpanels, VAICOM etc. define the same hooks.

`LuaExportDeployer` writes the script to `Scripts/DCS.AIAutomator/DCSMcpBridgeExport.lua` and
only *appends* a guarded `dofile(...)` to `Scripts/Export.lua` if missing (backing up to
`.bak` first). It's idempotent: already wired + identical companion script → "Already
deployed", writes nothing.

Verified against a live DCS session: deploy into Saved Games, the Lua listener, and the app's
connection all work alongside WWT/Tacview/DCS-BIOS exports. Still unverified live: the
`multiplayer` field and `send_atc_instruction` driven by a real MCP client.

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
- **Logging:** `DcsMcpBridgeHost` uses only `AddDebug()`. A GUI-subsystem app has no console;
  don't add `AddConsole()` back.
- **`FolderPicker` needs HWND interop**:
  `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window))` before
  `PickSingleFolderAsync()` (see `SettingsWindow.xaml.cs`).
