# DcsMcpBridge

Class library exposing an MCP server (via `ModelContextProtocol.AspNetCore`) that bridges an
LLM to DCS World over its `Export.lua` TCP socket. Hosted in-process by `DCS.AIAutomator`
(`../DCS.AIAutomator`), which starts the HTTP-transport MCP server on a background task and
shows live bridge/DCS/mission status in its window.

`DcsMcpBridgeHost` is the entry point other hosts call: `StartAsync` builds and runs the MCP
server, `Status` exposes bridge/DCS/mission state for UI binding.

`LuaExportDeployer` deploys the DCS-side companion script into the Saved Games `Scripts` folder
under a per-app subdirectory (`Scripts\DCS.AIAutomator\`), wiring it into `Export.lua` by
appending a guarded `dofile(...)` line. Deploy is idempotent: if `Export.lua` is already wired
and the companion script is present and up-to-date, it reports "Already deployed" and writes
nothing.

See the repo root `CLAUDE.md` for the full architecture.
