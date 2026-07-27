using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Chat.Controllers;

/// <summary>
/// Sert les ressources front embarquees dans la dll (chargees par le client web).
/// Accessible sans authentification : le JS gere lui-meme l'auth via l'ApiClient.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("ChatPlugin")]
public class AssetsController : ControllerBase
{
    private static Stream? GetResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var full = $"Jellyfin.Plugin.Chat.Web.{name}";
        return asm.GetManifestResourceStream(full);
    }

    [HttpGet("client.js")]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var stream = GetResource("client.js");
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript; charset=utf-8");
    }

    [HttpGet("client.css")]
    [Produces("text/css")]
    public ActionResult GetClientStyle()
    {
        var stream = GetResource("client.css");
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "text/css; charset=utf-8");
    }
}
