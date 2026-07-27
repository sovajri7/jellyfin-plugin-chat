using System;

namespace Jellyfin.Plugin.Chat.Models;

/// <summary>
/// Un message de chat, dans le salon public ou dans une conversation privee.
/// </summary>
public class ChatMessage
{
    public long Id { get; set; }

    /// <summary>
    /// Identifiant de la conversation. "public" pour le salon public,
    /// sinon la cle de conversation privee (voir <see cref="DirectRoomId"/>).
    /// </summary>
    public string RoomId { get; set; } = "public";

    public Guid SenderId { get; set; }

    public string SenderName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Type : "text" ou "image" (URL d'un GIF/image).
    /// </summary>
    public string Type { get; set; } = "text";

    public long Timestamp { get; set; }

    public bool Deleted { get; set; }

    /// <summary>
    /// Construit une cle de conversation privee stable et symetrique
    /// (le meme identifiant quel que soit l'emetteur).
    /// </summary>
    public static string DirectRoomId(Guid a, Guid b)
    {
        var first = a.CompareTo(b) <= 0 ? a : b;
        var second = a.CompareTo(b) <= 0 ? b : a;
        return "dm:" + first.ToString("N") + ":" + second.ToString("N");
    }
}
