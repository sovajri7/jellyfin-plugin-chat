using Jellyfin.Plugin.Chat.Data;
using Jellyfin.Plugin.Chat.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Chat;

/// <summary>
/// Enregistre les services du plugin dans le conteneur DI de Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ChatDatabase>();
        serviceCollection.AddSingleton<UserResolver>();
        serviceCollection.AddSingleton<PresenceTracker>();
        serviceCollection.AddSingleton<RateLimiter>();

        // Service d'amorcage : injecte le script du chat dans index.html au demarrage.
        serviceCollection.AddHostedService<WebInjectionService>();
    }
}
