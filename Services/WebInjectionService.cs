using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chat.Services;

/// <summary>
/// Injecte (ou retire) la balise script du chat dans le index.html du client web.
/// C'est la methode standard pour ajouter une UI custom cote client Jellyfin.
/// A re-executer apres chaque mise a jour du serveur (fait automatiquement au demarrage).
/// </summary>
public sealed class WebInjectionService : IHostedService
{
    private const string Marker = "<!-- jellyfin-chat-plugin -->";
    private const string ScriptTag =
        Marker + "<script src=\"/ChatPlugin/client.js\" defer></script>" + Marker;

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<WebInjectionService> _logger;

    public WebInjectionService(IServerApplicationPaths paths, ILogger<WebInjectionService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var enabled = Plugin.Instance?.Configuration.InjectClientScript ?? true;
            var indexPath = Path.Combine(_paths.WebPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("[Chat] index.html introuvable ({Path}), injection ignoree.", indexPath);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexPath);
            var alreadyInjected = html.Contains(Marker, StringComparison.Ordinal);

            if (enabled && !alreadyInjected)
            {
                var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    _logger.LogWarning("[Chat] Balise </body> absente, injection impossible.");
                    return Task.CompletedTask;
                }

                html = html.Insert(idx, ScriptTag);
                File.WriteAllText(indexPath, html);
                _logger.LogInformation("[Chat] Script du chat injecte dans le client web.");
            }
            else if (!enabled && alreadyInjected)
            {
                html = RemoveInjection(html);
                File.WriteAllText(indexPath, html);
                _logger.LogInformation("[Chat] Script du chat retire du client web.");
            }
        }
        catch (UnauthorizedAccessException)
        {
            var indexPath = Path.Combine(_paths.WebPath, "index.html");
            _logger.LogError(
                "[Chat] Ecriture refusee sur {Path}. Le process Jellyfin n'a pas les droits d'ecriture "
                + "sur le client web. Solution : rendre ce fichier accessible en ecriture, ex. "
                + "'sudo chown jellyfin {Path}' puis redemarrer Jellyfin. "
                + "Alternative robuste : installer le plugin 'File Transformation'.",
                indexPath, indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Chat] Echec de l'injection dans index.html.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string RemoveInjection(string html)
    {
        int start;
        while ((start = html.IndexOf(Marker, StringComparison.Ordinal)) >= 0)
        {
            var end = html.IndexOf(Marker, start + Marker.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                html = html.Remove(start, Marker.Length);
                break;
            }

            html = html.Remove(start, end + Marker.Length - start);
        }

        return html;
    }
}
