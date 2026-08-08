# DcsMcpBridge

Class library exposing an MCP server (via `ModelContextProtocol.AspNetCore`) that bridges an
LLM to DCS World over its `Export.lua` TCP socket. Hosted in-process by `DCS.AIAutomator`
(`../DCS.AIAutomator`), which starts the HTTP-transport MCP server on a background task and
shows live bridge/DCS/mission status in its window.

`DcsMcpBridgeHost` is the entry point other hosts call: `StartAsync` builds and runs the MCP
server, `Status` exposes bridge/DCS/mission state for UI binding.

See the repo root `CLAUDE.md` for the full architecture.
