# DCS.AIAutomator.Mcp

The MCP server (`ModelContextProtocol.AspNetCore`, HTTP transport) that lets an LLM use DCS
World. It's hosted in-process by the `DCS.AIAutomator` app.

- `DcsMcpBridgeHost` is the entry point: `StartAsync` builds and runs the server, and `Status`
  exposes bridge, DCS and mission state for the UI.
- The MCP tools (`AtcTools`, `AircraftTools`), the Serilog pipeline (`DcsLogging`) and Claude
  Desktop's stdio relay (`McpStdioRelay`) also live here.

See the repo root `CLAUDE.md` for the full architecture.
