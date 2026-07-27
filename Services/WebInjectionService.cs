using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chat.Services;

/// <summary>
/// Charge le script du chat dans le client web. Deux strategies :
///  1. Si le plugin "File Transformation" est present : on enregistre une transformation
///     de index.html au moment ou il est servi (aucune ecriture disque, survit aux mises a jour).
///  2. Sinon : injection directe dans le fichier index.html sur disque (necessite les droits d'ecriture).
/// </summary>
public sealed class WebInjectionService : IHostedService
{
    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<WebInjectionService> _logger;

    public WebInjectionService(IServerApplicationPaths paths, ILogger<WebInjectionService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var enabled = Plugin.Instance?.Configuration.InjectClientScript ?? true;

        if (FileTransformationAssembly() is not null)
        {
            if (enabled)
            {
                _logger.LogInformation("[Chat] File Transformation detecte : enregistrement de l'injection au service de la page.");
                ScheduleFileTransformationRegistration();
            }

            // Avec File Transformation on ne touche jamais au disque.
            return Task.CompletedTask;
        }

        // Repli : injection directe sur disque.
        if (enabled)
        {
            _logger.LogInformation("[Chat] File Transformation absent : injection directe dans index.html (droits d'ecriture requis).");
            DiskInject(true);
        }
        else
        {
            DiskInject(false);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Strategie 1 : File Transformation (par reflexion, pas de dependance compile-time)
    // ------------------------------------------------------------------
    private static Assembly? FileTransformationAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jellyfin.Plugin.FileTransformation", StringComparison.Ordinal));

    private void ScheduleFileTransformationRegistration()
    {
        // L'ordre d'initialisation des plugins n'est pas garanti : on reessaie quelques fois.
        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= 12; attempt++)
            {
                try
                {
                    if (TryRegisterFileTransformation())
                    {
                        _logger.LogInformation("[Chat] Injection enregistree aupres de File Transformation.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Chat] Tentative {Attempt} d'enregistrement File Transformation echouee, nouvel essai.", attempt);
                }

                await Task.Delay(2500).ConfigureAwait(false);
            }

            _logger.LogWarning("[Chat] Echec de l'enregistrement aupres de File Transformation apres plusieurs tentatives. "
                + "Repli sur l'injection disque.");
            DiskInject(true);
        });
    }

    private static bool TryRegisterFileTransformation()
    {
        var ftAsm = FileTransformationAssembly();
        var piType = ftAsm?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        var register = piType?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
        if (register is null)
        {
            return false;
        }

        // Le parametre attendu est un Newtonsoft.Json.Linq.JObject : on le construit par reflexion.
        var jobjectType = register.GetParameters()[0].ParameterType;
        var parse = jobjectType.GetMethod("Parse", new[] { typeof(string) });
        if (parse is null)
        {
            return false;
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            id = "b1a7c3e0-c0de-4a5b-9f10-abcdef012345",
            fileNamePattern = "index\\.html",
            callbackAssembly = typeof(WebInjection).Assembly.FullName,
            callbackClass = typeof(WebInjection).FullName,
            callbackMethod = nameof(WebInjection.TransformIndexHtml)
        });

        var jobj = parse.Invoke(null, new object[] { payloadJson });
        register.Invoke(null, new[] { jobj });
        return true;
    }

    // ------------------------------------------------------------------
    // Strategie 2 : injection directe dans index.html
    // ------------------------------------------------------------------
    private void DiskInject(bool enable)
    {
        try
        {
            var indexPath = Path.Combine(_paths.WebPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("[Chat] index.html introuvable ({Path}), injection ignoree.", indexPath);
                return;
            }

            var html = File.ReadAllText(indexPath);
            var alreadyInjected = html.Contains(WebInjection.Marker, StringComparison.Ordinal);

            if (enable && !alreadyInjected)
            {
                var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    _logger.LogWarning("[Chat] Balise </body> absente, injection impossible.");
                    return;
                }

                File.WriteAllText(indexPath, html.Insert(idx, WebInjection.ScriptTag));
                _logger.LogInformation("[Chat] Script du chat injecte dans le client web.");
            }
            else if (!enable && alreadyInjected)
            {
                File.WriteAllText(indexPath, RemoveInjection(html));
                _logger.LogInformation("[Chat] Script du chat retire du client web.");
            }
        }
        catch (UnauthorizedAccessException)
        {
            var indexPath = Path.Combine(_paths.WebPath, "index.html");
            _logger.LogError(
                "[Chat] Ecriture refusee sur {Path}. Le process Jellyfin n'a pas les droits d'ecriture sur le client web. "
                + "Solution recommandee : installer le plugin 'File Transformation'. "
                + "Alternative : 'sudo chown jellyfin {Path}' puis redemarrer Jellyfin.",
                indexPath, indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Chat] Echec de l'injection dans index.html.");
        }
    }

    private static string RemoveInjection(string html)
    {
        int start;
        while ((start = html.IndexOf(WebInjection.Marker, StringComparison.Ordinal)) >= 0)
        {
            var end = html.IndexOf(WebInjection.Marker, start + WebInjection.Marker.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                html = html.Remove(start, WebInjection.Marker.Length);
                break;
            }

            html = html.Remove(start, end + WebInjection.Marker.Length - start);
        }

        return html;
    }
}
