using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var ftPresent = FileTransformationAssembly() is not null;

        // 1) File Transformation : couvre les setups ou Jellyfin sert lui-meme le client web.
        if (ftPresent && enabled)
        {
            _logger.LogInformation("[Chat] File Transformation detecte : enregistrement de l'injection au service de la page.");
            ScheduleFileTransformationRegistration();
        }

        // 2) Injection directe dans index.html : couvre les setups ou un reverse proxy sert
        //    le dossier web en statique (File Transformation ne voit alors jamais la requete).
        //    Idempotent (marqueur) : n'ajoute rien si le script est deja present.
        DiskInject(enabled, ftPresent);

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

            _logger.LogWarning("[Chat] Echec de l'enregistrement aupres de File Transformation apres plusieurs tentatives.");
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
    private void DiskInject(bool enable, bool ftPresent)
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

            if (enable)
            {
                // Deja a jour (bonne version) : rien a faire.
                if (html.Contains(WebInjection.ScriptTag, StringComparison.Ordinal))
                {
                    return;
                }

                // Enleve toute ancienne injection (version obsolete ou ajout manuel) puis reinjecte.
                var cleaned = RemoveInjection(html);
                var idx = cleaned.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    _logger.LogWarning("[Chat] Balise </body> absente, injection impossible.");
                    return;
                }

                File.WriteAllText(indexPath, cleaned.Insert(idx, WebInjection.ScriptTag));
                _logger.LogInformation("[Chat] Script du chat injecte/mis a jour (v{Version}).", WebInjection.Version);
            }
            else
            {
                var cleaned = RemoveInjection(html);
                if (!string.Equals(cleaned, html, StringComparison.Ordinal))
                {
                    File.WriteAllText(indexPath, cleaned);
                    _logger.LogInformation("[Chat] Script du chat retire du client web.");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            var indexPath = Path.Combine(_paths.WebPath, "index.html");
            if (ftPresent)
            {
                // File Transformation gerera l'injection si Jellyfin sert lui-meme le client web.
                _logger.LogInformation(
                    "[Chat] index.html en lecture seule : File Transformation prend le relais si Jellyfin sert le client web. "
                    + "Si un reverse proxy sert le dossier web en statique, rends index.html accessible en ecriture "
                    + "('sudo chown jellyfin {Path}') pour que le plugin l'injecte lui-meme.",
                    indexPath);
            }
            else
            {
                _logger.LogError(
                    "[Chat] Ecriture refusee sur {Path}. Le process Jellyfin n'a pas les droits d'ecriture sur le client web. "
                    + "Solution : installer le plugin 'File Transformation', ou 'sudo chown jellyfin {Path}' puis redemarrer.",
                    indexPath, indexPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Chat] Echec de l'injection dans index.html.");
        }
    }

    private static string RemoveInjection(string html)
    {
        // 1) Bloc complet delimite par les marqueurs.
        html = Regex.Replace(
            html,
            Regex.Escape(WebInjection.Marker) + ".*?" + Regex.Escape(WebInjection.Marker),
            string.Empty,
            RegexOptions.Singleline);

        // 2) Toute balise script pointant vers notre client.js (y compris injection manuelle sans marqueur, toute version).
        html = Regex.Replace(
            html,
            "<script[^>]*ChatPlugin/client\\.js[^>]*>\\s*</script>",
            string.Empty,
            RegexOptions.IgnoreCase);

        // 3) Marqueurs orphelins eventuels.
        html = html.Replace(WebInjection.Marker, string.Empty, StringComparison.Ordinal);

        return html;
    }
}
