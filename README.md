# DCS.AIAutomator

A desktop app that lets an LLM control [DCS World](https://www.digitalcombatsim.com/) (the
flight simulator) through the [Model Context Protocol](https://modelcontextprotocol.io/),
bridging tool calls to a small Lua script the app deploys into DCS.

## What it does

- Runs an MCP server ([`ModelContextProtocol.AspNetCore`](https://github.com/modelcontextprotocol/csharp-sdk))
  in-process, over HTTP, exposing `get_aircraft_state` (the player aircraft's type, position,
  altitude, speeds, heading and malfunctions), `list_ai_flights` (the mission's AI aircraft and
  helicopters: callsign, group, type, coalition, position) and `send_atc_instruction` (shows an
  ATC instruction on screen in DCS; **Vectors**, **Orbit** and **Hold** also task the addressed
  AI flight: turn onto a heading, circle, or fly a racetrack, at a given or the current altitude;
  ClearToLand tasking is planned, #31).
- Maintains a persistent connection to that script's telemetry socket (`127.0.0.1:1024` by
  default).
- Shows live status in its window: whether the MCP bridge is up, whether DCS is connected, and
  — when a mission is active — the aircraft (by its DCS display name), mission name, and map,
  plus an expandable "Aircraft status" panel with the live aircraft state. Units (imperial or
  metric) are set in Settings > Display.
- Settings window (gear icon, top-right) configures the MCP server port, the DCS host/port, and
  DCS's install/Saved Games paths, and can generate + deploy the DCS-side script as
  `Saved Games\DCS\Scripts\Hooks\DCSMcpBridgeHooks.lua`. DCS loads Hooks scripts by itself at
  startup, so `Export.lua` is never modified and other export tools (DCS-BIOS, Tacview, …) are
  unaffected; restart DCS after deploying. Deploying also removes the `Export.lua`-based script
  earlier versions installed (keeping a `.bak`). Deploy is idempotent: an up-to-date script
  reports "Already deployed" and writes nothing. Saving restarts the bridge with the new
  settings.

## Running it

Deploy/run it from Visual Studio (Package and Publish, or F5) — that registers it as an
installed app. Once installed, launch it from the Start menu like any other app. Once running,
the MCP server is reachable at `http://127.0.0.1:5270/mcp`. Requests must carry the app's API
key as `Authorization: Bearer <key>`; anything else gets `401`.

**Connecting AI agents:** Settings (gear icon) → **AI agents** lists the supported agents found
on this PC:
- **Claude Code:** **Connect** registers the server (URL + key) for your user. If the `claude`
  command can't be run, use **Copy command** and run it yourself. After changing the port or
  regenerating the key, the app offers to update it.
- **Claude Desktop:** **Connect** adds an entry that runs this app as a small relay. No key is
  stored in Claude Desktop's config, so it never needs updating. Restart Claude Desktop
  afterwards. If Claude Desktop has never created its config file, open Claude Desktop →
  Settings → Developer → **Edit Config** once first. DCS.AIAutomator must be running for Claude
  Desktop to use it. For other MCP clients, use **Copy** next to the API key and configure the bearer header
yourself.

The main window's **AI CLIENTS** lamp shows **ACTIVE** when an agent has called in the last
minute and **IDLE** after that (with the agents' names). **AUTH FAILED** means an agent was
rejected for an old or wrong key; reconnect it in Settings → AI agents.

**Connecting DCS:** Settings → DCS Integration → **Deploy Lua scripts**, then restart DCS. The
deployed script holds a secret shared with this app, and DCS only talks to an app that knows it.
Deploy also asks to **allow mission scripting**: two lines in `Saved Games\DCS\Config\autoexec.cfg`
that DCS requires before the script can show messages in a mission. Your other settings there are
kept, and a `.bak` is made. Decline, and everything else still works; only messages fail, with an error.
If the DCS indicator shows **AUTH FAILED** or **SCRIPT OUTDATED** (the deployed script is from
another version of this app), redeploy and restart DCS.

`dotnet run --project src/DCS.AIAutomator -p:Platform=x64` doesn't reliably work for this app —
see the `dotnet run` gotcha in `CLAUDE.md` if you hit `COMException 0x80040154
(REGDB_E_CLASSNOTREG)`.

## Project layout

| Path | What it is |
|---|---|
| `src/DCS.AIAutomator` | WinUI 3 app — the thing you actually run. Status window + starts the bridge. |
| `src/DCS.AIAutomator.Core` | Class library — status model, DCS connection, Lua script deployment. |
| `src/DCS.AIAutomator.Mcp` | Class library — the MCP server (HTTP), its tools, logging, Claude Desktop relay. |
| `src/DCS.AIAutomator.Agents` | Class library — connecting AI agents (Claude Code, Claude Desktop). |
| `test/DCS.AIAutomator.UnitTests` | Tool logic, parsing, Lua generation, agent config edits; no real DCS/network needed. |
| `test/DCS.AIAutomator.IntegrationTests` | Starts a real bridge in-process and drives it with an MCP client. |

Build/test everything with `dotnet build DcsMcp.slnx` / `dotnet test DcsMcp.slnx`.

See [`CLAUDE.md`](CLAUDE.md) for architecture details, the DCS-side telemetry wire contract,
and known gotchas.
