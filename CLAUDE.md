# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

An MCP (Model Context Protocol) server that lets an LLM control DCS World (the flight
simulator) through a TCP telemetry/command socket opened by a Lua script it deploys into DCS, plus a WinUI 3 desktop
app that hosts it and shows live status. `DCS.AIAutomator` is the application users actually
run; `DCS.AIAutomator.Core`, `.Mcp` and `.Agents` are libraries it (and tests) load in-process —
none has an entry point of its own.

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

Three libraries plus the app (#17). Every project's namespace matches its name. Dependencies
point one way:

```
DCS.AIAutomator (app) ──► DCS.AIAutomator.Agents ──┐
        │          └────► DCS.AIAutomator.Mcp ─────┼──► DCS.AIAutomator.Core
        └──────────────────────────────────────────┘
```

Agents and Mcp never reference each other. **Cross-cutting services:**
- Libraries depend on `Microsoft.Extensions.*` abstractions only (`ILogger<T>`,
  `ILoggerProvider`, DI).
- Concrete providers (Serilog, the Credential Locker, `ApplicationData`) are chosen in the app.
- Dependency-free shared helpers (`Secrets`) go in Core.
- Add a separate infrastructure project only once a cross-cutting service needs a dependency Core
  shouldn't have *and* more than one library uses it.

- **`src/DCS.AIAutomator.Core`** — class library (`IsAotCompatible=true`): the domain and
  everything DCS-side. It has no MCP SDK dependency.
  - `AddDcsScripting(status, dcsIp, dcsPort)` — the DI wiring: shared `BridgeStatus`
    singleton, `DcsConnection` as both `IDcsConnection` singleton and hosted service.
  - `AircraftStateFormatter`/`UnitConversion` — the **single** formatting path, shared by the
    `get_aircraft_state` tool and the main window's aircraft panel. Don't format units
    anywhere else, or the two will drift.
  - `DcsConnection` — `BackgroundService` owning the TCP connection to the DCS script (3s
    reconnect loop); sends Lua commands and parses each incoming line as telemetry into
    `BridgeStatus`. **Keep dependencies one-directional (tool → connection, never back).**
    A predecessor had a tool ↔ bridge cycle, and the circular DI resolution recursed with
    zero output on stdout/stderr — indistinguishable from a startup hang.
  - `BridgeStatus` — observable status model. Its constructor takes no dependencies so the
    same instance survives host restarts (settings change → `StopAsync`/`StartAsync`);
    otherwise a UI subscribed to `Changed` goes stale. It also carries `McpEndpoint`/
    `DcsEndpoint` — render those in UI, never hardcoded literals.
  - `LuaHooksScriptGenerator` / `LuaScriptDeployer` / `DcsPathValidator` — generate and
    deploy the DCS-side script (below). Pure string building / file I/O against a passed-in
    path.
  - `Secrets`, `McpClientActivity` (the AI CLIENTS lamp's state), `MissionInfo`,
    `AircraftState`.
- **`src/DCS.AIAutomator.Mcp`** — class library, the MCP server.
  - `DcsMcpBridgeHost` — the composition root: builds a `WebApplication` (`AddDcsScripting`,
    `WithHttpTransport`, `WithTools<...>`, `MapMcp("/mcp")`), with the bearer check, the
    tool-call logging filter and client-activity recording. Exposes `Status`.
  - `AtcTools` — MCP tool `send_atc_instruction`, declared with SDK attributes
    (`[McpServerTool]`, `[Description]`), not hand-built JSON schema. Every instruction is
    shown on screen (`DcsCommands.ShowMessageAsync`). **Vectors** (#29) also tasks the flight:
    `AiFlight.Resolve` finds it among `ListFlightsAsync`'s flights (exact group name first, then
    callsign in any form; a shared callsign or no match is an error listing candidates). Then
    `VectorAsync` sets a 200 km route on the heading, at an altitude given in the user's units
    (converted to meters here) or the current one. The player's own flight is never tasked,
    only messaged. ATC headings are **magnetic**: the script converts them to a true course with
    the variation measured at the player's aircraft (`LoGetSelfData().Heading` minus
    `LoGetMagneticYaw()`), and without a player aircraft says it flew the heading as true. Hold,
    Orbit and ClearToLand are message-only until #30/#31. Depends on `IDcsConnection` and
    `BridgeStatus` (units), so tests use `FakeDcsConnection` (per-command `Responses`).
  - `AircraftTools` — MCP tool `get_aircraft_state`; reads the latest snapshot from
    `BridgeStatus` only (never the connection).
  - `FlightTools` — MCP tool `list_ai_flights` (#28): AI air groups from
    `DcsCommands.ListFlightsAsync`, formatted with `AircraftStateFormatter` in the user's units.
    Callsigns are shown spoken (`AiFlight.SpokenCallsign`: "Enfield11" → "Enfield 1-1").
  - `McpStdioRelay` — Claude Desktop's stdio ↔ HTTP pipe (#15, below).
  - `DcsLogging` — the Serilog pipeline (see Gotchas). Only the app uses it; it lives here so
    tests can reach it.
- **`src/DCS.AIAutomator.Agents`** — class library: registering this server with AI agents
  (#15). It has no MCP SDK or Core dependency.
  - `IAgentIntegration` + one class per agent (`AgentIntegrations.cs`).
  - `ClaudeCodeRegistration` builds the `claude mcp` arguments; `ClaudeCodeRegistrar` runs them.
  - `ClaudeDesktopConfig` — edits Claude Desktop's config JSON.
  - The app passes the URL and key in as `Func<string>`s.
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
    `BridgeStatus.Aircraft` (~5 Hz) in `BridgeStatus.Units`. The **AI CLIENTS** lamp renders
    `BridgeStatus.McpClients` (`McpClientActivity`). The server is stateless, so there's no
    connect/disconnect to see: ACTIVE means a request in the last 60 s, then IDLE. AUTH FAILED
    means a recent 401 with no success since. The host records successes in a message filter
    (name from `clientInfo`) and 401s in the bearer middleware. A 1 s timer re-renders the lamp.
  - `SettingsWindow` — nothing persists until **Save**, which writes via `SettingsService`
    and calls `App.ApplySettingsAsync(restartBridge)`. The bridge (and so the DCS connection)
    restarts only when the MCP port or DCS host/port changed; everything else, including
    units (held on the shared `BridgeStatus`), applies without a restart.
  - `Themes/ThemeResources.xaml` — brushes (per-theme `ThemeDictionaries`), fonts, shared
    styles; merged into `App.xaml`.
  - What stays here:
    - **UI-thread services:** `NotificationService` and `BridgeStatusNotifier` need
      `DispatcherQueue`.
    - **Packaged-only services:** `SettingsService` (`ApplicationData`), `SecretStore`
      (Credential Locker), and `McpRelayMode`, which uses both.
- **`test/DCS.AIAutomator.UnitTests`** — xUnit; tools against `FakeDcsConnection`, parser
  against raw JSON lines, generator/deployer against generated Lua / temp dirs, agent config
  edits against temp files. The library namespaces are global usings in the csproj.
- **`test/DCS.AIAutomator.IntegrationTests`** — starts a real `DcsMcpBridgeHost` in-process on
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
are kept in sync by hand. Two things catch drift (#21):
- **Protocol version — bump `LuaHooksScriptGenerator.ProtocolVersion` whenever the contract
  changes** (a field or message added, renamed or removed, either direction). The script sends it
  in the handshake reply (`{"authOk":true,"protocol":N}`). Any other value, or none (a script
  that predates versioning, or the handshake itself), sets `BridgeStatus.DcsScriptOutdated` →
  annunciator **SCRIPT OUTDATED** + "redeploy Lua scripts, restart DCS" toast. The app drops
  that connection and keeps retrying, and never uses an outdated script's data. Cleared like
  `DcsAuthFailed`.
- **Shared samples:** `DcsWireSamples` (unit tests) holds one sample line per message type. The
  parser tests parse them, and `DcsWireContractTests` asserts that the generated Lua's JSON keys,
  the samples' keys and the C# DTOs' `[JsonPropertyName]`s are the same set. A new field or
  message means a new sample, or that test fails. It checks names only, not types or nesting.

### Commands: structured, never Lua (#27)

The app never sends Lua. `IDcsConnection.SendCommandAsync(cmd, args)` writes one JSON line
(`{"cmd":"message","id":7,"text":"...","seconds":10}`) and waits for the script's
`{"commandResult":{"id":7,"ok":true}}` or `{..."ok":false,"error":"..."}`. Replies are matched
by id (they can arrive out of order). No connection, a timeout (`DefaultCommandTimeout`, 5 s:
paused, hung or in the menus), a disconnect, or `ok:false` all come back as a failed
`DcsCommandResult`, never as success. Command bodies are never logged.

In the script, `net.json2lua` decodes the line and `mcpBridgeCommands[cmd]` handles it. Each
handler validates its own parameters, and an unknown `cmd` gets an error reply. There's no
`loadstring`. **Adding a capability = a handler in the generator + a typed method in
`DcsCommands` + a `ProtocolVersion` bump.** Never add a "run this Lua" command.

Handlers that need mission scripting (`trigger.*`, `Group.*`) build their code themselves,
quoting values with `%q`, and run it with `net.dostring_in("scripting", code)`. That code must
return `"ok"`, `"ok "` followed by JSON (a query; it becomes `commandResult.data`, e.g.
`listFlights`' array), or an error message. Query code builds its JSON by hand and must
JSON-escape every string (there's no JSON library in mission scripting). Keep its object keys
literal in the generator, so `DcsWireContractTests` sees them, and add the DTO
(`AiFlightTelemetry`) to that test's type list. DCS allows `dostring_in` only when
`Saved Games\DCS\Config\autoexec.cfg` has `net.allow_unsafe_api` ∋ `"userhooks"` and
`net.allow_dostring_in` ∋ `"scripting"` (`Sim_ControlAPI.md`). **Deploy** offers to add exactly that
(`AutoexecConfig`), with a consent dialog: it appends to existing lists rather than replacing
them, keeps a `.bak`, and is idempotent. Declining still deploys. Mission commands then fail
with "mission scripting isn't enabled". Commands outside a mission get "no mission is running".
Verified live (probes in a running mission): `dostring_in` returns `result, success`; a target
that autoexec doesn't allow returns **nothing** ("not enabled"); a script error comes back as the
result with `success == false`. **Don't use `a_do_script`** (the docs' suggestion, via the
`"mission"` target): it runs the code but drops its return value, so success can't be confirmed.
The appended (`list[#list + 1] = ...`) autoexec form is honoured.

**Check every DCS API call against the stock docs before using it** —
`DCS World/API/Sim_ControlAPI.md` (Hooks: `Sim.*`, callbacks, which `Export.Lo*` calls work
there) and `DCS World/Scripts/Export.lua` (the `Lo*` list). The script once relied on
`LoGetMissionInfo`, which doesn't exist; `pcall` swallowed the error and the app showed "No
active mission" forever. A speculative `multiplayer` field was removed (multiplayer is deferred
to v2).

### Authentication: MCP API key and DCS link secret (#7)

Two independent secrets, both from `Secrets.NewSecret()` (32 random bytes, base64url), stored
by the app in the Windows Credential Locker (`SecretStore`, app project only) and passed to the
libraries as parameters. **There is no "auth off" mode**: `DcsMcpBridgeHost.StartAsync` throws
without both.

- **MCP API key:** every request to the MCP server must carry `Authorization: Bearer <key>`,
  checked by middleware *before* MCP handling (`Secrets.BearerMatches`, constant-time), or it
  gets `401`. `DcsMcpBridgeHost.SetApiKey` rotates it live (no restart, DCS stays connected).
  Settings → Connection shows it masked (Show / Copy / Regenerate…) and has **Register with
  Claude Code**, which runs `claude mcp remove`, then `claude mcp add --transport http --scope
  user dcs-aiautomator <url> --header "Authorization: Bearer <key>"` (`ClaudeCodeRegistration`
  builds the args, checked against Claude Code 2.1.283; `--header` is variadic so it goes after
  name/url). It goes through `ArgumentList`, never a shell, and CLI output is redacted before it's
  shown or logged.
- **DCS link secret:** baked into the deployed Hooks script (deploy/generate take it as a
  parameter; it must be base64url since it sits in a Lua string literal). After connecting,
  `DcsConnection` sends `AUTH <secret>` first. The script runs no command and sends nothing
  until that matches, then replies `{"authOk":true,"protocol":N}`. On a mismatch it replies
  `{"authError":true}` and closes. A client that doesn't authenticate within `AUTH_TIMEOUT` (2 s)
  is dropped, so a stray process can't hold the single client slot. **`DcsConnected` means
  authenticated**, not just TCP-connected. A script that rejects the secret sets
  `BridgeStatus.DcsAuthFailed` → annunciator **AUTH FAILED** + "redeploy Lua scripts, restart
  DCS" toast. It's cleared by a successful handshake, or when DCS can't be reached at all. A
  script that streams without the handshake (deployed before #7) is **SCRIPT OUTDATED** instead
  (see the protocol version above).

Neither secret is ever logged. Tests assert this for the connection, the host, and tool calls.

### AI agents and the Claude Desktop relay (#15)

Settings → **AI agents** lists only the agents detected on this PC, each with Connect /
Disconnect. One `IAgentIntegration` per agent (`AgentIntegrations.cs`, app project). Add a class
for a new agent (OpenAI/Gemini come later); the page needs no change.

- **Claude Code** (`ClaudeCodeAgent`): detected by `claude` on PATH; connected state from `claude
  mcp get dcs-aiautomator` (exit 0/1); Connect/Disconnect via `claude mcp add/remove` (#7). Its
  entry embeds the URL and key, so a key regeneration or port change offers to update it.
- **Claude Desktop** (`ClaudeDesktopAgent`): `claude_desktop_config.json` only takes **stdio**
  servers, and its remote "Connectors" can't reach `127.0.0.1`. So the entry launches **this app's
  own exe** as a relay: `{"command": "%LOCALAPPDATA%\\Microsoft\\WindowsApps\\dcs-aiautomator.exe",
  "args": ["--mcp-relay"]}`, through the package's **app execution alias**
  (`Package.appxmanifest`). `ClaudeDesktopConfig` edits only our key.
  - **Relay mode:** `Program.cs` is a custom `Main` (`DISABLE_XAML_GENERATED_MAIN`). With
    `--mcp-relay` it runs `McpRelayMode` and never starts WinUI. Otherwise it does exactly what
    the XAML compiler generates. Running as the packaged app, the relay reads the port from
    `SettingsService` and the key from the Credential Locker (`SecretStore.TryGet`; it never mints
    one). So Claude Desktop's config holds **no URL and no key**, and never needs updating.
  - **`McpStdioRelay` (in `DCS.AIAutomator.Mcp`) must stay a dumb pipe:** it forwards JSON-RPC between
    a stdio transport and `HttpClientTransport` (+ bearer header), hosts no tools and has no DCS
    logic. That's what keeps it from being the deleted stdio-exe bridge. If the app is down or
    rejects the key, each request gets a JSON-RPC error saying so (no hang). stdout carries only
    JSON-RPC; the relay logs to its own `dcs-aiautomator-relay-*.clef` file.
  - **MSIX AppData virtualization:** a packaged app's *edits to existing* files under
    `%APPDATA%` go to the real file, but files it *creates* there are redirected to a private,
    per-package copy that other apps can't see. So Connect refuses when Claude Desktop has no
    config file yet (the user creates it via Claude Desktop → Settings → Developer → Edit Config),
    and backups go to the app's `LocalCache\Backups`, not next to the config. The alternative,
    the `unvirtualizedResources` restricted capability, is documented as not intended for apps
    like this.

Verified live (Claude Code 2.1.283, Claude Desktop, F/A-18C):
- **Claude Desktop:** Connect + restart → `get_aircraft_state` returns data through the relay.
  This confirms the alias-launched relay has package identity: it reads the settings and the
  Credential Locker. With the app closed, Claude Desktop reports a clear "not running" error,
  with no hang.
- **Claude Code:** Disconnect/Connect from the page update `claude mcp get`. After Regenerate,
  the old key gets 401 (AI CLIENTS → AUTH FAILED) until the offered update + `/mcp` reconnect.
- **Missing config:** Connect refuses with the Edit Config hint; backups land in
  `LocalCache\Backups`.
- **AI CLIENTS lamp:** shows both agents by their `clientInfo` names, ACTIVE → IDLE.

### DCS-side script: a Hooks script, and DCS is the socket server

The DCS side is a **Hooks script** (`Saved Games\DCS\Scripts\Hooks\DCSMcpBridgeHooks.lua`), not
an `Export.lua` companion. Only the Hooks (GUI) environment has `Sim.*` — mission name
(`Sim.getMissionName()`), map (`Sim.getCurrentMission().mission.theatre`), and DCS's own
aircraft display names (`Sim.getUnitTypeAttribute(type, "DisplayName")`) — while still exposing
every Export call as `Export.Lo*`. DCS loads `Scripts/Hooks/*.lua` itself **at startup**
(restart DCS after deploying), so nothing edits the user's `Export.lua` and there's no hook
chaining with DCS-BIOS/Tacview/etc. `local Sim = Sim or DCS` covers older DCS versions.

`DcsConnection` connects *out*, so the script listens: a non-blocking LuaSocket server, polled
from `onSimulationFrame` (accept, dispatch queued commands, write one telemetry line).
`onSimulationStart` caches mission name/map; `onSimulationStop` sends `missionActive:false`.
The socket stays open between missions since the script lives as long as DCS. Commands are
handled in the **GUI** Lua state; handlers that need the mission reach it through
`net.dostring_in` (see Commands above).

`LuaScriptDeployer` writes the Hooks script, and migrates installs from earlier versions: it
removes our `dofile(...)` lines from `Export.lua` (other tools' lines untouched, `.bak` kept)
and deletes the old `DCSMcpBridgeExport.lua` scripts — left in place they'd bind the same port.
Idempotent: nothing to migrate + identical script → "Already deployed", writes nothing.

Verified against a live DCS session (single-player, F/A-18C quick-start): the Hooks script
deploys, DCS listens, the app connects, and the readout shows the correct mission name, map
and aircraft display name. Still unverified live: `send_atc_instruction` showing its message
in DCS (#27 live check).

The deployer only accepts a real Saved Games folder (`DcsPathValidator`: must contain `Config`,
must not contain `bin\DCS.exe`/`bin-mt\DCS.exe`). The install folder also has `Config` and a
`Scripts` folder, so it was easy to pick by mistake. The default path comes from the Windows
Saved Games known folder, not `%USERPROFILE%`, because users relocate it (e.g. `E:\SavedGames`).

## Commands

```bash
dotnet build DcsMcp.slnx     # build everything (WinUI platform x64 is pinned in the .slnx)
dotnet test DcsMcp.slnx      # unit + integration tests
dotnet test test/DCS.AIAutomator.UnitTests --filter FullyQualifiedName~AtcToolsTests   # single test class
```

CI (`.github/workflows/ci.yml`) runs that same build and test on `windows-latest` for every PR
to `master` and every push to it. Test `.trx` results are uploaded as the `test-results`
artifact. `dotnet build` doesn't produce the MSIX package, so CI needs no signing certificate;
packaging belongs to the release pipeline (#22).

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
  only — never in a library (`src/DCS.AIAutomator.*`), since integration tests run the
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
  fights AOT/trimming) by `DcsLogging` in `src/DCS.AIAutomator.Mcp`: CLEF JSON lines, rolled daily into
  `dcs-aiautomator-yyyyMMdd.clef`, deleted after `LogRetentionDays` (default 7, by age via
  `retainedFileTimeLimit`). The app resolves the folder (`LocalCacheFolder\Logs`) and passes
  `DcsLogging.Provider` to `DcsMcpBridgeHost.StartAsync(loggerProvider:)` and uses it for its own
  loggers, so one file has everything. Core sees only `Microsoft.Extensions.Logging`.
  Without a provider (tests) the host falls back to `AddDebug()`. Never add `AddConsole()` — a
  GUI-subsystem app has no console. Use structured templates (`"{Tool}"`), not interpolation.
  **Never log secrets, tool arguments, or command bodies** — the tool-call filter logs only
  name/outcome/duration, and an integration test asserts arguments never reach the log. DCS
  Hooks-script errors are forwarded as `{"log":{...}}` lines (rate-limited per message in Lua),
  logged under the `DCS` category, and raised as `BridgeStatus.DcsScriptError` for a toast.
- **`FolderPicker` needs HWND interop**:
  `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window))` before
  `PickSingleFolderAsync()` (see `SettingsWindow.xaml.cs`).
