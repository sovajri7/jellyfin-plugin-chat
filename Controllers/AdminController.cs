using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chat.Data;
using Jellyfin.Plugin.Chat.Models;
using Jellyfin.Plugin.Chat.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Chat.Controllers;

/// <summary>Actions de moderation reservees aux administrateurs.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ChatPlugin/admin")]
[Produces("application/json")]
public class AdminController : ChatControllerBase
{
    private readonly ChatDatabase _db;
    private readonly UserResolver _users;

    public AdminController(IAuthorizationContext auth, ChatDatabase db, UserResolver users)
        : base(auth)
    {
        _db = db;
        _users = users;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static bool TryParse(string id, out Guid guid) =>
        Guid.TryParseExact(id, "N", out guid) || Guid.TryParse(id, out guid);

    /// <summary>Vider entierement un salon (public ou DM).</summary>
    [HttpDelete("room/{roomId}")]
    public ActionResult ClearRoom(string roomId)
    {
        var count = _db.ClearRoom(roomId);
        return Ok(new { cleared = count });
    }

    /// <summary>Supprimer un message precis.</summary>
    [HttpDelete("message/{id:long}")]
    public ActionResult DeleteMessage(long id)
    {
        var ok = _db.SoftDeleteMessage(id);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Etat de moderation de tous les utilisateurs sanctionnes.</summary>
    [HttpGet("moderation")]
    public ActionResult GetModeration()
    {
        var now = Now();
        var list = _db.ListModeration().Select(m => new
        {
            userId = m.UserId.ToString("N"),
            name = _users.GetName(m.UserId),
            banned = m.Banned,
            muted = m.Muted,
            expiresAt = m.ExpiresAt,
            active = m.IsActive(now),
            reason = m.Reason
        });
        return Ok(list);
    }

    /// <summary>Bannir un utilisateur (lecture + ecriture bloquees).</summary>
    [HttpPost("ban")]
    public ActionResult Ban([FromBody] ModerationRequest req)
    {
        if (!TryParse(req.UserId, out var user))
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        var existing = _db.GetModeration(user) ?? new ModerationEntry { UserId = user };
        existing.Banned = true;
        existing.ExpiresAt = req.DurationMinutes > 0
            ? Now() + req.DurationMinutes * 60_000
            : 0;
        existing.Reason = req.Reason ?? string.Empty;
        _db.SetModeration(existing);
        return Ok(new { status = "banned" });
    }

    /// <summary>Reduire au silence un utilisateur (lecture ok, ecriture bloquee).</summary>
    [HttpPost("mute")]
    public ActionResult Mute([FromBody] ModerationRequest req)
    {
        if (!TryParse(req.UserId, out var user))
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        var existing = _db.GetModeration(user) ?? new ModerationEntry { UserId = user };
        existing.Muted = true;
        existing.ExpiresAt = req.DurationMinutes > 0
            ? Now() + req.DurationMinutes * 60_000
            : 0;
        existing.Reason = req.Reason ?? string.Empty;
        _db.SetModeration(existing);
        return Ok(new { status = "muted" });
    }

    /// <summary>Lever toutes les sanctions d'un utilisateur.</summary>
    [HttpPost("clear/{userId}")]
    public ActionResult ClearSanctions(string userId)
    {
        if (!TryParse(userId, out var user))
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        _db.ClearModeration(user);
        return Ok(new { status = "cleared" });
    }
}
