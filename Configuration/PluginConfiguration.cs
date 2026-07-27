using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Chat.Configuration;

/// <summary>
/// Configuration persistante du plugin (editable depuis le panel admin).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Injecte automatiquement le script du chat dans le client web au demarrage.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Nombre max de messages conserves par salon (0 = illimite).
    /// </summary>
    public int MaxMessagesPerRoom { get; set; } = 5000;

    /// <summary>
    /// Longueur max d'un message.
    /// </summary>
    public int MaxMessageLength { get; set; } = 2000;

    /// <summary>
    /// Autorise le salon public (visible par tous les utilisateurs).
    /// </summary>
    public bool EnablePublicRoom { get; set; } = true;

    /// <summary>
    /// Autorise les messages prives entre utilisateurs.
    /// </summary>
    public bool EnableDirectMessages { get; set; } = true;

    /// <summary>
    /// Autorise l'envoi de GIF / images par URL.
    /// </summary>
    public bool EnableMedia { get; set; } = true;
}
