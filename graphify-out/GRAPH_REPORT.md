# Graph Report - .  (2026-08-09)

## Corpus Check
- Corpus is ~7,496 words - fits in a single context window. You may not need a graph.

## Summary
- 173 nodes · 228 edges · 15 communities (13 shown, 2 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 3 edges (avg confidence: 0.83)
- Token cost: 14,289 input · 2,061 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Project Structure & Dependencies|Project Structure & Dependencies]]
- [[_COMMUNITY_Unit Test Suite|Unit Test Suite]]
- [[_COMMUNITY_DCS Connection & ATC Tools|DCS Connection & ATC Tools]]
- [[_COMMUNITY_Main Window UI Rendering|Main Window UI Rendering]]
- [[_COMMUNITY_Bridge Host & Integration Tests|Bridge Host & Integration Tests]]
- [[_COMMUNITY_Settings Window UI|Settings Window UI]]
- [[_COMMUNITY_App Lifecycle & Theme|App Lifecycle & Theme]]
- [[_COMMUNITY_Settings Persistence & Assets|Settings Persistence & Assets]]
- [[_COMMUNITY_Telemetry Parsing|Telemetry Parsing]]
- [[_COMMUNITY_Lua Export Deployment|Lua Export Deployment]]
- [[_COMMUNITY_Bridge README|Bridge README]]
- [[_COMMUNITY_Store Logo Asset|Store Logo Asset]]

## God Nodes (most connected - your core abstractions)
1. `MainWindow` - 16 edges
2. `DcsConnection` - 15 edges
3. `App` - 14 edges
4. `SettingsWindow` - 12 edges
5. `McpServerIntegrationTests` - 10 edges
6. `BridgeStatus` - 8 edges
7. `DcsMcpBridgeHost` - 8 edges
8. `SettingsService` - 7 edges
9. `LuaExportDeployerTests` - 7 edges
10. `LuaExportDeployer` - 6 edges

## Surprising Connections (you probably didn't know these)
- `MainWindow` --references--> `ThemeResources`  [INFERRED]
  src/DCS.AIAutomator/MainWindow.xaml.cs → src/DCS.AIAutomator/Themes/ThemeResources.xaml
- `AtcToolsTests` --calls--> `AtcTools`  [EXTRACTED]
  test/DcsMcpBridge.UnitTests/AtcToolsTests.cs → src/DCS.Scripting/AtcTools.cs
- `Lock Screen Logo` --references--> `App (WinUI)`  [INFERRED]
  src/DCS.AIAutomator/Assets/LockScreenLogo.scale-200.png → src/DCS.AIAutomator/App.xaml.cs
- `Splash Screen` --references--> `App (WinUI)`  [INFERRED]
  src/DCS.AIAutomator/Assets/SplashScreen.scale-200.png → src/DCS.AIAutomator/App.xaml.cs
- `FakeDcsConnection` --implements--> `IDcsConnection`  [EXTRACTED]
  test/DcsMcpBridge.UnitTests/FakeDcsConnection.cs → src/DCS.Scripting/IDcsConnection.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **MCP Server Implementation Flow** — dcsmcpbridge_dcsmcpbridgehost, dcsmcpbridge_atctools, dcsmcpbridge_dcsconnection, dcsmcpbridge_idcsconnection [EXTRACTED 1.00]
- **DCS Telemetry Processing** — dcs_world_export_lua, dcsmcpbridge_dcsconnection, dcsmcpbridge_dcstelemetryparser, dcsmcpbridge_bridgestatus [EXTRACTED 1.00]
- **WinUI Application Components** — dcs_aiautomator_app, dcs_aiautomator_mainwindow, dcs_aiautomator_settingswindow, dcs_aiautomator_settingsservice [EXTRACTED 1.00]

## Communities (15 total, 2 thin omitted)

### Community 0 - "Project Structure & Dependencies"
Cohesion: 0.09
Nodes (22): net10.0-windows10.0.19041.0, Microsoft.Windows.SDK.BuildTools, Microsoft.WindowsAppSDK, Microsoft.NET.Sdk, net10.0, ModelContextProtocol.AspNetCore, Microsoft.NET.Sdk, net10.0 (+14 more)

### Community 1 - "Unit Test Suite"
Cohesion: 0.12
Nodes (6): AtcToolsTests, DcsTelemetryParserTests, LuaExportDeployerTests, LuaExportScriptGeneratorTests, Fact, IDisposable

### Community 2 - "DCS Connection & ATC Tools"
Cohesion: 0.10
Nodes (12): AtcAction, BackgroundService, AtcTools, DcsConnection, IDcsConnection, FakeDcsConnection, Description, ILogger (+4 more)

### Community 3 - "Main Window UI Rendering"
Cohesion: 0.12
Nodes (11): bool, BridgeState, Color, MainWindow, ThemeResources, BridgeStatus, DcsScriptingServiceCollectionExtensions, DispatcherQueue (+3 more)

### Community 4 - "Bridge Host & Integration Tests"
Cohesion: 0.16
Nodes (9): CancellationToken, DcsMcpBridgeHost, McpServerIntegrationTests, IAsyncDisposable, IAsyncLifetime, McpClient, Task, ValueTask (+1 more)

### Community 5 - "Settings Window UI"
Cohesion: 0.20
Nodes (5): SettingsWindow, Func, NavigationView, NavigationViewSelectionChangedEventArgs, RoutedEventArgs

### Community 6 - "App Lifecycle & Theme"
Cohesion: 0.23
Nodes (6): Application, App, ElementTheme, LaunchActivatedEventArgs, Window, WindowEventArgs

### Community 7 - "Settings Persistence & Assets"
Cohesion: 0.22
Nodes (5): ApplicationDataContainer, App (WinUI), SettingsService, Lock Screen Logo, Splash Screen

### Community 8 - "Telemetry Parsing"
Cohesion: 0.22
Nodes (6): AtcJsonContext, DcsTelemetryJsonContext, DcsTelemetryMessage, DcsTelemetryParser, JsonSerializerContext, MissionInfo

### Community 9 - "Lua Export Deployment"
Cohesion: 0.22
Nodes (4): LuaExportDeployer, LuaExportScriptGenerator, DeployResult, string

## Knowledge Gaps
- **28 isolated node(s):** `net10.0-windows10.0.19041.0`, `Microsoft.Windows.SDK.BuildTools`, `Microsoft.WindowsAppSDK`, `Microsoft.NET.Sdk`, `net10.0` (+23 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **2 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `DcsConnection` connect `DCS Connection & ATC Tools` to `Telemetry Parsing`, `Lua Export Deployment`, `Main Window UI Rendering`?**
  _High betweenness centrality (0.188) - this node is a cross-community bridge._
- **Why does `MainWindow` connect `Main Window UI Rendering` to `Lua Export Deployment`, `Settings Window UI`, `App Lifecycle & Theme`?**
  _High betweenness centrality (0.148) - this node is a cross-community bridge._
- **Why does `App` connect `App Lifecycle & Theme` to `Bridge Host & Integration Tests`, `Settings Window UI`, `Settings Persistence & Assets`?**
  _High betweenness centrality (0.124) - this node is a cross-community bridge._
- **What connects `net10.0-windows10.0.19041.0`, `Microsoft.Windows.SDK.BuildTools`, `Microsoft.WindowsAppSDK` to the rest of the system?**
  _28 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Project Structure & Dependencies` be split into smaller, more focused modules?**
  _Cohesion score 0.0873015873015873 - nodes in this community are weakly interconnected._
- **Should `Unit Test Suite` be split into smaller, more focused modules?**
  _Cohesion score 0.11594202898550725 - nodes in this community are weakly interconnected._
- **Should `DCS Connection & ATC Tools` be split into smaller, more focused modules?**
  _Cohesion score 0.09881422924901186 - nodes in this community are weakly interconnected._