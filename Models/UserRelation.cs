using System;

namespace Jellyfin.Plugin.Chat.Models;

public enum RelationKind
{
    /// <summary>Demande d'ami envoyee, en attente d'acceptation.</summary>
    PendingRequest = 0,

    /// <summary>Amis (relation acceptee).</summary>
    Friend = 1,

    /// <summary>Utilisateur bloque.</summary>
    Blocked = 2
}

/// <summary>
/// Relation dirigee entre deux utilisateurs.
/// Amitie : stockee de facon symetrique (deux lignes) une fois acceptee.
/// Blocage : dirige (Owner bloque Target).
/// Demande : dirige (Owner a envoye la demande a Target).
/// </summary>
public class UserRelation
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public Guid TargetId { get; set; }

    public RelationKind Kind { get; set; }

    public long UpdatedAt { get; set; }
}
