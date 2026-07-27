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

/// <summary>Gestion des amis et des blocages.</summary>
[ApiController]
[Authorize]
[Route("ChatPlugin/relations")]
[Produces("application/json")]
public class FriendsController : ChatControllerBase
{
    private readonly ChatDatabase _db;
    private readonly UserResolver _users;

    public FriendsController(IAuthorizationContext auth, ChatDatabase db, UserResolver users)
        : base(auth)
    {
        _db = db;
        _users = users;
    }

    private bool TryParseUser(string id, out Guid guid) =>
        (Guid.TryParseExact(id, "N", out guid) || Guid.TryParse(id, out guid))
        && _users.GetUser(guid) is not null;

    /// <summary>Liste de mes amis et demandes.</summary>
    [HttpGet]
    public async Task<ActionResult> ListRelations()
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        var mine = _db.ListRelations(me);
        var inbound = _db.ListInbound(me)
            .Where(r => r.Kind == RelationKind.PendingRequest)
            .Select(r => r.OwnerId).ToHashSet();

        ChatUserDto? Dto(Guid id, string relation)
        {
            var u = _users.GetUser(id);
            if (u is null)
            {
                return null;
            }

            var dto = _users.ToDto(u);
            dto.Relation = relation;
            return dto;
        }

        return Ok(new
        {
            friends = mine.Where(r => r.Kind == RelationKind.Friend)
                .Select(r => Dto(r.TargetId, "friend")).Where(d => d is not null),
            outgoing = mine.Where(r => r.Kind == RelationKind.PendingRequest)
                .Select(r => Dto(r.TargetId, "pending")).Where(d => d is not null),
            incoming = inbound.Select(id => Dto(id, "incoming")).Where(d => d is not null),
            blocked = mine.Where(r => r.Kind == RelationKind.Blocked)
                .Select(r => Dto(r.TargetId, "blocked")).Where(d => d is not null)
        });
    }

    /// <summary>Envoyer une demande d'ami (ou accepter directement une demande recue).</summary>
    [HttpPost("request/{userId}")]
    public async Task<ActionResult> SendRequest(string userId)
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        if (!TryParseUser(userId, out var other) || other == me)
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        if (_db.IsBlockedBetween(me, other))
        {
            return StatusCode(403, new { error = "Action impossible (blocage)." });
        }

        // L'autre m'a deja envoye une demande -> on devient amis directement.
        if (_db.GetRelation(other, me) == RelationKind.PendingRequest)
        {
            _db.SetRelation(me, other, RelationKind.Friend);
            _db.SetRelation(other, me, RelationKind.Friend);
            return Ok(new { status = "friend" });
        }

        _db.SetRelation(me, other, RelationKind.PendingRequest);
        return Ok(new { status = "pending" });
    }

    /// <summary>Accepter une demande recue.</summary>
    [HttpPost("accept/{userId}")]
    public async Task<ActionResult> Accept(string userId)
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        if (!TryParseUser(userId, out var other))
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        if (_db.GetRelation(other, me) != RelationKind.PendingRequest)
        {
            return BadRequest(new { error = "Aucune demande en attente de cet utilisateur." });
        }

        _db.SetRelation(me, other, RelationKind.Friend);
        _db.SetRelation(other, me, RelationKind.Friend);
        return Ok(new { status = "friend" });
    }

    /// <summary>Refuser une demande recue, annuler une demande envoyee, ou retirer un ami.</summary>
    [HttpPost("remove/{userId}")]
    public async Task<ActionResult> Remove(string userId)
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        if (!TryParseUser(userId, out var other))
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        _db.RemoveRelation(me, other);
        // Retirer aussi le cote symetrique si c'etait une amitie.
        if (_db.GetRelation(other, me) == RelationKind.Friend)
        {
            _db.RemoveRelation(other, me);
        }

        return Ok(new { status = "none" });
    }

    /// <summary>Bloquer un utilisateur (rompt l'amitie et les demandes).</summary>
    [HttpPost("block/{userId}")]
    public async Task<ActionResult> Block(string userId)
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        if (!TryParseUser(userId, out var other) || other == me)
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        // Nettoyer l'eventuelle amitie/demande de l'autre cote.
        if (_db.GetRelation(other, me) is RelationKind.Friend or RelationKind.PendingRequest)
        {
            _db.RemoveRelation(other, me);
        }

        _db.SetRelation(me, other, RelationKind.Blocked);
        return Ok(new { status = "blocked" });
    }

    /// <summary>Debloquer un utilisateur.</summary>
    [HttpPost("unblock/{userId}")]
    public async Task<ActionResult> Unblock(string userId)
    {
        var me = await GetCurrentUserIdAsync().ConfigureAwait(false);
        if (!TryParseUser(userId, out var other))
        {
            return BadRequest(new { error = "Utilisateur invalide." });
        }

        if (_db.GetRelation(me, other) == RelationKind.Blocked)
        {
            _db.RemoveRelation(me, other);
        }

        return Ok(new { status = "none" });
    }
}
