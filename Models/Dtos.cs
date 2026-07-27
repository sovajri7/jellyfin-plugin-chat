using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Chat.Models;

/// <summary>Utilisateur expose au client (annuaire, liste d'amis...).</summary>
public class ChatUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>URL de l'avatar Jellyfin (Primary), ou null si aucune image.</summary>
    public string? AvatarUrl { get; set; }

    public bool IsAdmin { get; set; }

    /// <summary>Relation du demandeur vers cet utilisateur : none|pending|incoming|friend|blocked.</summary>
    public string Relation { get; set; } = "none";

    /// <summary>Cet utilisateur m'a-t-il bloque ?</summary>
    public bool BlockedMe { get; set; }
}

public class MessageDto
{
    public long Id { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string? SenderAvatarUrl { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public long Timestamp { get; set; }
    public bool Deleted { get; set; }
    public bool Mine { get; set; }
}

public class SendMessageRequest
{
    public string RoomId { get; set; } = "public";

    /// <summary>Pour un DM : id de l'autre utilisateur (alternative a RoomId).</summary>
    public string? TargetUserId { get; set; }

    public string Content { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
}

public class ModerationRequest
{
    public string UserId { get; set; } = string.Empty;
    public long DurationMinutes { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class SelfState
{
    public bool Banned { get; set; }
    public bool Muted { get; set; }
    public long MuteExpiresAt { get; set; }
    public List<string> BlockedByMe { get; set; } = new();

    /// <summary>La recherche de GIF Klipy est-elle disponible (cle configuree) ?</summary>
    public bool GifEnabled { get; set; }
}
