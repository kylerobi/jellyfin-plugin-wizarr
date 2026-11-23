using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Wizarr;

/// <summary>
/// Registers <see cref="LoginLinkStartup"/> with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<LoginLinkStartup>();
    }
}

/// <summary>
/// Re-applies the login-screen invite link when Jellyfin starts, e.g. after the base URL changed
/// or the link was removed from the branding settings by hand.
/// </summary>
public class LoginLinkStartup : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Plugin.Instance?.ApplyLoginLink();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
