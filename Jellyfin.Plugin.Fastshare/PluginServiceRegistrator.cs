using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>Registrace služeb pluginu do DI kontejneru Jellyfinu.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<FsState>();
        serviceCollection.AddSingleton<IHostedService, DownloadWorkerService>();
        serviceCollection.AddSingleton<IHostedService, WatcherService>();
        serviceCollection.AddSingleton<LibraryAdvisor>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LibraryAdvisor>());
    }
}
