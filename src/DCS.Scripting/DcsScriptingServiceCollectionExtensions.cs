using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DCS.Scripting;

/// <summary>
/// DI registration for the DCS.Scripting service library. Registers the observable
/// <see cref="BridgeStatus"/>, the persistent <see cref="DcsConnection"/> (as both a singleton
/// resolved via <see cref="IDcsConnection"/> for tools and a hosted service for its connect
/// loop), and wires the DCS socket address.
/// </summary>
public static class DcsScriptingServiceCollectionExtensions
{
    /// <summary>
    /// Adds the DCS scripting services to the container. Pass an existing <paramref name="status"/>
    /// to keep a caller's <see cref="BridgeStatus"/> instance (and its UI subscriptions) alive
    /// across container rebuilds, e.g. when the bridge host is restarted after a settings change.
    /// </summary>
    public static IServiceCollection AddDcsScripting(
        this IServiceCollection services,
        BridgeStatus? status = null,
        string dcsIp = "127.0.0.1",
        int dcsPort = 1024)
    {
        services.AddSingleton(status ?? new BridgeStatus());
        services.AddSingleton(sp => new DcsConnection(
            sp.GetRequiredService<ILogger<DcsConnection>>(),
            sp.GetRequiredService<BridgeStatus>(),
            dcsIp,
            dcsPort));
        services.AddSingleton<IDcsConnection>(sp => sp.GetRequiredService<DcsConnection>());
        services.AddHostedService(sp => sp.GetRequiredService<DcsConnection>());
        return services;
    }
}
