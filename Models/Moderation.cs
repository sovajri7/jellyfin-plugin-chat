using System;

namespace Jellyfin.Plugin.Chat.Models;

/// <summary>
/// Sanction de moderation appliquee a un utilisateur (ban ou mute).
/// </summary>
public class ModerationEntry
{
    public Guid UserId { get; set; }

    /// <summary>Banni : ne peut plus ni lire ni ecrire.</summary>
    public bool Banned { get; set; }

    /// <summary>Rendu muet : peut lire mais pas ecrire.</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Fin de la sanction (epoch ms). 0 = permanent tant que la sanction est active.
    /// </summary>
    public long ExpiresAt { get; set; }

    public string Reason { get; set; } = string.Empty;

    public long UpdatedAt { get; set; }

    /// <summary>La sanction est-elle encore active a l'instant donne ?</summary>
    public bool IsActive(long nowMs) => ExpiresAt == 0 || ExpiresAt > nowMs;
}
