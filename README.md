# DCS.AIAutomator

A desktop app that lets an LLM control [DCS World](https://www.digitalcombatsim.com/) (the
flight simulator) through the [Model Context Protocol](https://modelcontextprotocol.io/),
bridging tool calls to DCS's `Export.lua` socket.

## What it does

- Runs an MCP server ([`ModelContextProtocol.AspNetCore`](https://github.com/modelcontextprotocol/csharp-sdk))
  in-process, over HTTP, exposing a `send_atc_instruction` tool that transmits ATC-style
  instructions to aircraft in a running mission.
- Maintains a persistent connection to DCS's `Export.lua` telemetry socket
  (`127.0.0.1:1024` by default).
- Shows live status in its window: whether the MCP bridge is up, whether DCS is connected, and
  — when a mission is active — the aircraft, mission name, terrain, and single/multiplayer mode.
- Settings window (gear icon, top-right) configures the MCP server port, the DCS host/port, and
  DCS's install/Saved Games paths, and can generate + deploy the `Export.lua` companion script
  DCS needs into the Saved Games `Scripts` folder (safe to run alongside other Export.lua tools —
  it appends rather than overwrites). Saving restarts the bridge with the new settings.

## Running it

Deploy/run it from Visual Studio (Package and Publish, or F5) — that registers it as an
installed app. Once installed, launch it from the Start menu like any other app. Once running,
the MCP server is reachable at `http://127.0.0.1:5270/mcp`; point an MCP client at that URL to
use the `send_atc_instruction` tool.

`dotnet run --project src/DCS.AIAutomator -p:Platform=x64` doesn't reliably work for this app —
see the `dotnet run` gotcha in `CLAUDE.md` if you hit `COMException 0x80040154
(REGDB_E_CLASSNOTREG)`.

## Project layout

| Path | What it is |
|---|---|
| `src/DCS.AIAutomator` | WinUI 3 app — the thing you actually run. Status window + starts the bridge. |
| `src/DcsMcpBridge` | Class library — the MCP server and DCS connection, hosted in-process by the app above. |
| `test/DcsMcpBridge.UnitTests` | Tool logic and telemetry parsing, no real DCS/network needed. |
| `test/DcsMcpBridge.IntegrationTests` | Starts a real bridge in-process and drives it with an MCP client. |

Build/test everything with `dotnet build DcsMcp.slnx` / `dotnet test DcsMcp.slnx`.

See [`CLAUDE.md`](CLAUDE.md) for architecture details, the DCS-side telemetry wire contract,
and known gotchas.
