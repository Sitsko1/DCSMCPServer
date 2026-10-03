using System;
using System.Linq;
using System.Threading;
using DCS.Scripting;
using Microsoft.UI.Dispatching;

namespace DCS.AIAutomator;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so the same executable can run headless as
/// Claude Desktop's stdio relay: <c>dcs-aiautomator.exe --mcp-relay</c> (via the package's app
/// execution alias) never starts WinUI or opens a window. Otherwise this is exactly what the XAML
/// compiler generates.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains(ClaudeDesktopConfig.RelayArgument))
        {
            return McpRelayMode.Run();
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
