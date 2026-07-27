using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chat.Controllers;

/// <summary>
/// Proxy serveur vers l'API Klipy (klipy.com) pour la recherche de GIF.
/// La cle API reste cote serveur : elle n'est jamais exposee au navigateur.
/// </summary>
[ApiController]
[Authorize(Policy = "DefaultAuthorization")]
[Route("ChatPlugin/gif")]
[Produces("application/json")]
public class GifController : ChatControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GifController> _logger;

    public GifController(IAuthorizationContext auth, IHttpClientFactory httpFactory, ILogger<GifController> logger)
        : base(auth)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Recherche (ou tendances si q vide) de GIF via Klipy.</summary>
    [HttpGet("search")]
    public async Task<ActionResult> Search([FromQuery] string? q = null, [FromQuery] int page = 1)
    {
        var apiKey = Config.KlipyApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey) || !Config.EnableMedia)
        {
            return Ok(new { enabled = false, items = Array.Empty<object>() });
        }

        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        var customerId = me.ToString("N");
        var endpoint = string.IsNullOrWhiteSpace(q) ? "trending" : "search";
        var url = $"https://api.klipy.com/api/v1/{Uri.EscapeDataString(apiKey)}/gifs/{endpoint}"
                  + $"?per_page=24&page={page}&customer_id={customerId}";
        if (endpoint == "search")
        {
            url += "&q=" + Uri.EscapeDataString(q!.Trim());
        }

        try
        {
            var client = _httpFactory.CreateClient(NamedClient.Default);
            using var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Chat] Klipy a repondu {Status}", (int)resp.StatusCode);
                return Ok(new { enabled = true, items = Array.Empty<object>(), error = "klipy_" + (int)resp.StatusCode });
            }

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var items = new List<object>();
            bool hasNext = false;

            if (TryGetDataArray(doc.RootElement, out var arr, out hasNext))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var gif = ExtractGif(item);
                    if (gif is not null)
                    {
                        items.Add(gif);
                    }
                }
            }

            return Ok(new { enabled = true, items, hasNext });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Chat] Erreur lors de l'appel a Klipy.");
            return Ok(new { enabled = true, items = Array.Empty<object>(), error = "exception" });
        }
    }

    /// <summary>Localise le tableau de resultats quelle que soit l'enveloppe ({data:{data:[]}} ou {data:[]}).</summary>
    private static bool TryGetDataArray(JsonElement root, out JsonElement array, out bool hasNext)
    {
        array = default;
        hasNext = false;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
        {
            return false;
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            array = data;
            return true;
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("has_next", out var hn) && hn.ValueKind == JsonValueKind.True)
            {
                hasNext = true;
            }

            if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Array)
            {
                array = inner;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extrait de facon defensive une petite (preview) et une grande (url) version GIF d'un item.
    /// Klipy expose un objet "file"/"files" avec des tailles (sm/md/hd) contenant des variantes gif/webp/mp4.
    /// </summary>
    private static object? ExtractGif(JsonElement item)
    {
        var found = new List<(int Width, string Url)>();
        CollectGifUrls(item, found, 0);
        if (found.Count == 0)
        {
            return null;
        }

        found.Sort((a, b) => a.Width.CompareTo(b.Width));
        var preview = found[0].Url;                 // plus petite pour la vignette
        var full = found[^1].Url;                   // plus grande pour l'envoi
        return new { preview, url = full };
    }

    private static void CollectGifUrls(JsonElement node, List<(int, string)> acc, int depth)
    {
        if (depth > 6)
        {
            return;
        }

        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                // Un objet portant a la fois "url" (finissant par .gif) et eventuellement "width".
                if (node.TryGetProperty("url", out var urlProp)
                    && urlProp.ValueKind == JsonValueKind.String)
                {
                    var u = urlProp.GetString() ?? string.Empty;
                    if (u.Contains(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        var w = 0;
                        if (node.TryGetProperty("width", out var wp))
                        {
                            if (wp.ValueKind == JsonValueKind.Number)
                            {
                                w = wp.GetInt32();
                            }
                            else if (wp.ValueKind == JsonValueKind.String
                                     && int.TryParse(wp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pw))
                            {
                                w = pw;
                            }
                        }

                        acc.Add((w, u));
                    }
                }

                foreach (var prop in node.EnumerateObject())
                {
                    CollectGifUrls(prop.Value, acc, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var el in node.EnumerateArray())
                {
                    CollectGifUrls(el, acc, depth + 1);
                }

                break;
        }
    }
}
