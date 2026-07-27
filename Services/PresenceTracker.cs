using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.Chat.Services;

/// <summary>
/// Suivi de presence en memoire : un utilisateur est "en ligne" s'il a interroge
/// le plugin recemment (le client fait un polling regulier). Enregistre en singleton.
/// </summary>
public sealed class PresenceTracker
{
    private const long OnlineWindowMs = 30_000; // considere en ligne si vu il y a < 30 s
    private readonly ConcurrentDictionary<Guid, long> _lastSeen = new();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Marque un utilisateur comme actif maintenant.</summary>
    public void Touch(Guid userId) => _lastSeen[userId] = Now();

    /// <summary>Nombre d'utilisateurs actifs dans la fenetre de presence.</summary>
    public int OnlineCount()
    {
        var cutoff = Now() - OnlineWindowMs;
        return _lastSeen.Values.Count(ts => ts >= cutoff);
    }

    /// <summary>Un utilisateur donne est-il en ligne ?</summary>
    public bool IsOnline(Guid userId) =>
        _lastSeen.TryGetValue(userId, out var ts) && ts >= Now() - OnlineWindowMs;
}
