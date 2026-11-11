using System.Net.Http;
using Jellyfin.Plugin.Napisy24.Activation;
using Jellyfin.Plugin.Napisy24.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Napisy24;

/// <summary>
/// Registers plugin services. Jellyfin does not scan plugin assemblies for
/// <see cref="ISubtitleProvider"/> implementations, so the provider must be
/// registered explicitly.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddHttpClient(Napisy24Provider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

        serviceCollection.AddSingleton<ActivationService>();
        serviceCollection.AddSingleton<ISubtitleProvider, Napisy24Provider>();
    }
}
