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

/// <summary>Endpoints principaux du chat : annuaire, messages, envoi, etat perso.</summary>
[ApiController]
[Authorize(Policy = "DefaultAuthorization")]
[Route("ChatPlugin")]
[Produces("application/json")]
public class ChatController : ChatControllerBase
{
    private readonly ChatDatabase _db;
    private readonly UserResolver _users;

    public ChatController(IAuthorizationContext auth, ChatDatabase db, UserResolver users)
        : base(auth)
    {
        _db = db;
        _users = users;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance!.Configuration;

    /// <summary>Annuaire des utilisateurs du serveur (nom + avatar + relation avec moi).</summary>
    [HttpGet("users")]
    public async Task<ActionResult<IEnumerable<ChatUserDto>>> GetUsers()
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        var myRelations = _db.ListRelations(me).ToDictionary(r => r.TargetId, r => r.Kind);
        var inbound = _db.ListInbound(me);
        var pendingFromThem = inbound
            .Where(r => r.Kind == RelationKind.PendingRequest)
            .Select(r => r.OwnerId).ToHashSet();
        var blockedMe = inbound
            .Where(r => r.Kind == RelationKind.Blocked)
            .Select(r => r.OwnerId).ToHashSet();

        var result = new List<ChatUserDto>();
        foreach (var user in _users.AllUsers())
        {
            if (user.Id == me)
            {
                continue;
            }

            var dto = _users.ToDto(user);
            dto.BlockedMe = blockedMe.Contains(user.Id);
            if (myRelations.TryGetValue(user.Id, out var kind))
            {
                dto.Relation = kind switch
                {
                    RelationKind.Friend => "friend",
                    RelationKind.Blocked => "blocked",
                    RelationKind.PendingRequest => "pending",
                    _ => "none"
                };
            }
            else if (pendingFromThem.Contains(user.Id))
            {
                dto.Relation = "incoming";
            }

            result.Add(dto);
        }

        return Ok(result.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Mon etat : suis-je banni/muet, qui ai-je bloque.</summary>
    [HttpGet("self")]
    public async Task<ActionResult<SelfState>> GetSelf()
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        var mod = _db.GetModeration(me);
        var now = Now();
        var state = new SelfState
        {
            Banned = mod is not null && mod.Banned && mod.IsActive(now),
            Muted = mod is not null && mod.Muted && mod.IsActive(now),
            MuteExpiresAt = mod?.ExpiresAt ?? 0,
            BlockedByMe = _db.ListRelations(me)
                .Where(r => r.Kind == RelationKind.Blocked)
                .Select(r => r.TargetId.ToString("N"))
                .ToList()
        };
        return Ok(state);
    }

    /// <summary>Messages d'un salon (polling avec ?after=dernierId).</summary>
    [HttpGet("messages")]
    public async Task<ActionResult<IEnumerable<MessageDto>>> GetMessages(
        [FromQuery] string roomId = "public",
        [FromQuery] string? targetUserId = null,
        [FromQuery] long after = 0,
        [FromQuery] bool history = false)
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);

        var resolvedRoom = ResolveRoom(roomId, targetUserId, me, out var otherUser, out var error);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        // Un banni ne lit rien.
        var mod = _db.GetModeration(me);
        if (mod is not null && mod.Banned && mod.IsActive(Now()))
        {
            return Ok(Array.Empty<MessageDto>());
        }

        // DM avec quelqu'un qui m'a bloque (ou que j'ai bloque) : pas d'echange.
        if (otherUser is not null && _db.IsBlockedBetween(me, otherUser.Value))
        {
            return Ok(Array.Empty<MessageDto>());
        }

        var msgs = history
            ? _db.GetHistory(resolvedRoom, 100)
            : _db.GetMessages(resolvedRoom, after, 200);

        return Ok(msgs.Select(m => ToDto(m, me)));
    }

    /// <summary>Envoi d'un message.</summary>
    [HttpPost("messages")]
    public async Task<ActionResult<MessageDto>> SendMessage([FromBody] SendMessageRequest req)
    {
        var user = await GetCurrentUserAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var me = user.Id;
        var content = (req.Content ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            return BadRequest(new { error = "Message vide." });
        }

        if (content.Length > Config.MaxMessageLength)
        {
            content = content[..Config.MaxMessageLength];
        }

        var type = req.Type == "image" && Config.EnableMedia ? "image" : "text";

        // Sanctions.
        var mod = _db.GetModeration(me);
        if (mod is not null && mod.IsActive(Now()))
        {
            if (mod.Banned)
            {
                return StatusCode(403, new { error = "Vous etes banni du chat." });
            }

            if (mod.Muted)
            {
                return StatusCode(403, new { error = "Vous etes reduit au silence." });
            }
        }

        var resolvedRoom = ResolveRoom(req.RoomId, req.TargetUserId, me, out var otherUser, out var error);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        if (resolvedRoom == "public" && !Config.EnablePublicRoom)
        {
            return StatusCode(403, new { error = "Salon public desactive." });
        }

        if (otherUser is not null)
        {
            if (!Config.EnableDirectMessages)
            {
                return StatusCode(403, new { error = "Messages prives desactives." });
            }

            if (_db.IsBlockedBetween(me, otherUser.Value))
            {
                return StatusCode(403, new { error = "Echange impossible (blocage)." });
            }
        }

        var msg = _db.InsertMessage(new ChatMessage
        {
            RoomId = resolvedRoom,
            SenderId = me,
            SenderName = user.Username,
            Content = content,
            Type = type,
            Timestamp = Now()
        });

        _db.PruneRoom(resolvedRoom, Config.MaxMessagesPerRoom);
        return Ok(ToDto(msg, me));
    }

    /// <summary>Supprimer MON propre message.</summary>
    [HttpDelete("messages/{id:long}")]
    public async Task<ActionResult> DeleteOwnMessage(long id)
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        var sender = _db.GetMessageSender(id);
        if (sender is null)
        {
            return NotFound();
        }

        if (sender.Value != me)
        {
            return Forbid();
        }

        _db.SoftDeleteMessage(id);
        return NoContent();
    }

    // ---------- helpers ----------

    private string ResolveRoom(string? roomId, string? targetUserId, Guid me, out Guid? otherUser, out string? error)
    {
        otherUser = null;
        error = null;

        if (!string.IsNullOrEmpty(targetUserId))
        {
            if (!Guid.TryParseExact(targetUserId, "N", out var other) &&
                !Guid.TryParse(targetUserId, out other))
            {
                error = "Utilisateur cible invalide.";
                return string.Empty;
            }

            if (_users.GetUser(other) is null)
            {
                error = "Utilisateur cible introuvable.";
                return string.Empty;
            }

            otherUser = other;
            return ChatMessage.DirectRoomId(me, other);
        }

        if (string.IsNullOrEmpty(roomId) || roomId == "public")
        {
            return "public";
        }

        // Room DM deja resolue cote client : verifier que j'en fais partie.
        if (roomId.StartsWith("dm:", StringComparison.Ordinal))
        {
            var parts = roomId.Split(':');
            if (parts.Length == 3 &&
                Guid.TryParseExact(parts[1], "N", out var a) &&
                Guid.TryParseExact(parts[2], "N", out var b))
            {
                if (a != me && b != me)
                {
                    error = "Acces refuse a cette conversation.";
                    return string.Empty;
                }

                otherUser = a == me ? b : a;
                return roomId;
            }
        }

        error = "Salon invalide.";
        return string.Empty;
    }

    private MessageDto ToDto(ChatMessage m, Guid me)
    {
        var sender = _users.GetUser(m.SenderId);
        return new MessageDto
        {
            Id = m.Id,
            RoomId = m.RoomId,
            SenderId = m.SenderId.ToString("N"),
            SenderName = m.SenderName,
            SenderAvatarUrl = sender is null ? null : _users.GetAvatarUrl(sender),
            Content = m.Deleted ? string.Empty : m.Content,
            Type = m.Type,
            Timestamp = m.Timestamp,
            Deleted = m.Deleted,
            Mine = m.SenderId == me
        };
    }
}
