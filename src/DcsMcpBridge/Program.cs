using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

var builder = Host.CreateApplicationBuilder(args);

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Persistent TCP bridge to DCS's Export.lua socket; registered as both a singleton (so tools
// can inject it) and a hosted service (so its connect loop runs for the app's lifetime).
builder.Services.AddSingleton(sp =>
    new DcsConnection(sp.GetRequiredService<ILogger<DcsConnection>>(), dcsIp: "127.0.0.1", dcsPort: 1024));
builder.Services.AddSingleton<IDcsConnection>(sp => sp.GetRequiredService<DcsConnection>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DcsConnection>());

// AtcTools' AtcAction enum needs its own JSON metadata merged into the default (reflection-free)
// serializer options — see AtcJsonContext.
var atcToolSerializerOptions = new JsonSerializerOptions
{
    TypeInfoResolverChain = { McpJsonUtilities.DefaultOptions.TypeInfoResolver!, AtcJsonContext.Default }
};

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RandomNumberTools>()
    .WithTools<AtcTools>(atcToolSerializerOptions);

await builder.Build().RunAsync();
