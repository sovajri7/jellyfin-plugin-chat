using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Chat.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Chat;

/// <summary>
/// Plugin principal. Point d'entree charge par Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Instance singleton, pratique pour recuperer la config depuis les services.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    public override string Name => "Chat en Direct";

    public override Guid Id => Guid.Parse("6b3d2c1a-9e4f-4b2a-8c7d-1f0a2b3c4d5e");

    public override string Description =>
        "Messagerie temps reel entre utilisateurs du serveur : salon public, messages prives, amis, blocage et moderation.";

    /// <summary>
    /// Chemin du dossier de donnees du plugin (base SQLite, etc.).
    /// </summary>
    public string DataFolderPath => ApplicationPaths.PluginConfigurationsPath;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
