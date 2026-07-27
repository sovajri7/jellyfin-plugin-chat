using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Chat.Services;

/// <summary>
/// Anti-flood en memoire : limite le nombre de messages par utilisateur sur une fenetre glissante.
/// Empeche qu'un compte se serve du chat pour spammer / saturer la base. Enregistre en singleton.
/// </summary>
public sealed class RateLimiter
{
    private const int MaxPerWindow = 15;      // max 15 messages
    private const long WindowMs = 10_000;     // par tranche de 10 s
    private readonly ConcurrentDictionary<Guid, Queue<long>> _hits = new();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Retourne false si l'utilisateur depasse la limite (message a rejeter).</summary>
    public bool Allow(Guid userId)
    {
        var now = Now();
        var q = _hits.GetOrAdd(userId, _ => new Queue<long>());
        lock (q)
        {
            while (q.Count > 0 && q.Peek() < now - WindowMs)
            {
                q.Dequeue();
            }

            if (q.Count >= MaxPerWindow)
            {
                return false;
            }

            q.Enqueue(now);
            return true;
        }
    }
}
