using System;

namespace Jellyfin.Plugin.Chat.Services;

/// <summary>
/// Cible de rappel invoquee par le plugin "File Transformation".
/// Doit rester publique et statique : File Transformation la resout par reflexion
/// (assembly + classe + methode) et l'appelle avec le contenu courant de index.html.
/// </summary>
public static class WebInjection
{
    public const string Marker = "<!-- jellyfin-chat-plugin -->";

    public const string ScriptTag =
        Marker + "<script src=\"/ChatPlugin/client.js\" defer></script>" + Marker;

    /// <summary>
    /// Recoit le contenu de index.html, y insere le script du chat avant &lt;/body&gt;,
    /// et renvoie le HTML transforme. Idempotent grace au marqueur.
    /// </summary>
    public static string TransformIndexHtml(TransformationPayload payload)
    {
        var html = payload?.Contents ?? string.Empty;
        if (html.Length == 0 || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? html : html.Insert(idx, ScriptTag);
    }
}

/// <summary>
/// Forme de l'objet passe par File Transformation : { "contents": "..." }.
/// La deserialisation (cote File Transformation, via Newtonsoft) est insensible a la casse.
/// </summary>
public class TransformationPayload
{
    public string Contents { get; set; } = string.Empty;
}
