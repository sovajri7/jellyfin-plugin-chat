using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Chat.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chat.Data;

/// <summary>
/// Acces SQLite unique du plugin. Enregistre en singleton.
/// SQLite n'accepte qu'un seul ecrivain : on serialise les ecritures via un verrou.
/// </summary>
public sealed class ChatDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private readonly ILogger<ChatDatabase> _logger;

    public ChatDatabase(ILogger<ChatDatabase> logger)
    {
        _logger = logger;
        var folder = Plugin.Instance?.DataFolderPath
                     ?? Path.Combine(Path.GetTempPath(), "jellyfin-chat");
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, "chat.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
        _logger.LogInformation("[Chat] Base de donnees initialisee : {Path}", dbPath);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS messages (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    room_id    TEXT NOT NULL,
    sender_id  TEXT NOT NULL,
    sender_name TEXT NOT NULL,
    content    TEXT NOT NULL,
    type       TEXT NOT NULL DEFAULT 'text',
    timestamp  INTEGER NOT NULL,
    deleted    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_messages_room ON messages(room_id, id);

CREATE TABLE IF NOT EXISTS relations (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id   TEXT NOT NULL,
    target_id  TEXT NOT NULL,
    kind       INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    UNIQUE(owner_id, target_id)
);
CREATE INDEX IF NOT EXISTS idx_relations_owner ON relations(owner_id);

CREATE TABLE IF NOT EXISTS moderation (
    user_id    TEXT PRIMARY KEY,
    banned     INTEGER NOT NULL DEFAULT 0,
    muted      INTEGER NOT NULL DEFAULT 0,
    expires_at INTEGER NOT NULL DEFAULT 0,
    reason     TEXT NOT NULL DEFAULT '',
    updated_at INTEGER NOT NULL
);";
            cmd.ExecuteNonQuery();
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- Messages ----------

    public ChatMessage InsertMessage(ChatMessage msg)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO messages (room_id, sender_id, sender_name, content, type, timestamp, deleted)
VALUES ($room, $sid, $sname, $content, $type, $ts, 0);
SELECT last_insert_rowid();";
            msg.Timestamp = msg.Timestamp == 0 ? Now() : msg.Timestamp;
            cmd.Parameters.AddWithValue("$room", msg.RoomId);
            cmd.Parameters.AddWithValue("$sid", msg.SenderId.ToString("N"));
            cmd.Parameters.AddWithValue("$sname", msg.SenderName);
            cmd.Parameters.AddWithValue("$content", msg.Content);
            cmd.Parameters.AddWithValue("$type", msg.Type);
            cmd.Parameters.AddWithValue("$ts", msg.Timestamp);
            msg.Id = (long)(cmd.ExecuteScalar() ?? 0L);
            return msg;
        }
    }

    /// <summary>
    /// Recupere les messages d'un salon, avec id > afterId pour le polling.
    /// </summary>
    public List<ChatMessage> GetMessages(string roomId, long afterId, int limit)
    {
        var list = new List<ChatMessage>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, room_id, sender_id, sender_name, content, type, timestamp, deleted
FROM messages
WHERE room_id = $room AND id > $after
ORDER BY id ASC
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadMessage(r));
        }

        return list;
    }

    /// <summary>Charge l'historique recent (les N derniers messages, ordre chronologique).</summary>
    public List<ChatMessage> GetHistory(string roomId, int limit)
    {
        var list = new List<ChatMessage>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, room_id, sender_id, sender_name, content, type, timestamp, deleted
FROM messages
WHERE room_id = $room
ORDER BY id DESC
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadMessage(r));
        }

        list.Reverse();
        return list;
    }

    private static ChatMessage ReadMessage(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        RoomId = r.GetString(1),
        SenderId = Guid.ParseExact(r.GetString(2), "N"),
        SenderName = r.GetString(3),
        Content = r.GetString(4),
        Type = r.GetString(5),
        Timestamp = r.GetInt64(6),
        Deleted = r.GetInt64(7) != 0
    };

    /// <summary>Suppression douce d'un message (remplace par "message supprime").</summary>
    public bool SoftDeleteMessage(long messageId)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET deleted = 1, content = '' WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", messageId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public Guid? GetMessageSender(long messageId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sender_id FROM messages WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", messageId);
        var res = cmd.ExecuteScalar() as string;
        return res is null ? null : Guid.ParseExact(res, "N");
    }

    /// <summary>Vide entierement un salon (action admin).</summary>
    public int ClearRoom(string roomId)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE room_id = $room;";
            cmd.Parameters.AddWithValue("$room", roomId);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Elague les messages au-dela de la limite configuree.</summary>
    public void PruneRoom(string roomId, int maxMessages)
    {
        if (maxMessages <= 0)
        {
            return;
        }

        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
DELETE FROM messages
WHERE room_id = $room
  AND id NOT IN (
    SELECT id FROM messages WHERE room_id = $room ORDER BY id DESC LIMIT $max
  );";
            cmd.Parameters.AddWithValue("$room", roomId);
            cmd.Parameters.AddWithValue("$max", maxMessages);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- Relations ----------

    public void SetRelation(Guid owner, Guid target, RelationKind kind)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO relations (owner_id, target_id, kind, updated_at)
VALUES ($o, $t, $k, $u)
ON CONFLICT(owner_id, target_id)
DO UPDATE SET kind = $k, updated_at = $u;";
            cmd.Parameters.AddWithValue("$o", owner.ToString("N"));
            cmd.Parameters.AddWithValue("$t", target.ToString("N"));
            cmd.Parameters.AddWithValue("$k", (int)kind);
            cmd.Parameters.AddWithValue("$u", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveRelation(Guid owner, Guid target)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM relations WHERE owner_id = $o AND target_id = $t;";
            cmd.Parameters.AddWithValue("$o", owner.ToString("N"));
            cmd.Parameters.AddWithValue("$t", target.ToString("N"));
            cmd.ExecuteNonQuery();
        }
    }

    public RelationKind? GetRelation(Guid owner, Guid target)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind FROM relations WHERE owner_id = $o AND target_id = $t;";
        cmd.Parameters.AddWithValue("$o", owner.ToString("N"));
        cmd.Parameters.AddWithValue("$t", target.ToString("N"));
        var res = cmd.ExecuteScalar();
        return res is null || res is DBNull ? null : (RelationKind)Convert.ToInt32(res, CultureInfo.InvariantCulture);
    }

    /// <summary>Toutes les relations dont je suis proprietaire.</summary>
    public List<UserRelation> ListRelations(Guid owner)
    {
        var list = new List<UserRelation>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, owner_id, target_id, kind, updated_at FROM relations WHERE owner_id = $o;";
        cmd.Parameters.AddWithValue("$o", owner.ToString("N"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new UserRelation
            {
                Id = r.GetInt64(0),
                OwnerId = Guid.ParseExact(r.GetString(1), "N"),
                TargetId = Guid.ParseExact(r.GetString(2), "N"),
                Kind = (RelationKind)r.GetInt32(3),
                UpdatedAt = r.GetInt64(4)
            });
        }

        return list;
    }

    /// <summary>Relations pointant VERS moi (pour connaitre demandes recues / qui m'a bloque).</summary>
    public List<UserRelation> ListInbound(Guid target)
    {
        var list = new List<UserRelation>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, owner_id, target_id, kind, updated_at FROM relations WHERE target_id = $t;";
        cmd.Parameters.AddWithValue("$t", target.ToString("N"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new UserRelation
            {
                Id = r.GetInt64(0),
                OwnerId = Guid.ParseExact(r.GetString(1), "N"),
                TargetId = Guid.ParseExact(r.GetString(2), "N"),
                Kind = (RelationKind)r.GetInt32(3),
                UpdatedAt = r.GetInt64(4)
            });
        }

        return list;
    }

    public bool IsBlockedBetween(Guid a, Guid b)
    {
        return GetRelation(a, b) == RelationKind.Blocked
               || GetRelation(b, a) == RelationKind.Blocked;
    }

    // ---------- Moderation ----------

    public void SetModeration(ModerationEntry entry)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO moderation (user_id, banned, muted, expires_at, reason, updated_at)
VALUES ($u, $b, $m, $e, $r, $t)
ON CONFLICT(user_id)
DO UPDATE SET banned=$b, muted=$m, expires_at=$e, reason=$r, updated_at=$t;";
            cmd.Parameters.AddWithValue("$u", entry.UserId.ToString("N"));
            cmd.Parameters.AddWithValue("$b", entry.Banned ? 1 : 0);
            cmd.Parameters.AddWithValue("$m", entry.Muted ? 1 : 0);
            cmd.Parameters.AddWithValue("$e", entry.ExpiresAt);
            cmd.Parameters.AddWithValue("$r", entry.Reason);
            cmd.Parameters.AddWithValue("$t", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public ModerationEntry? GetModeration(Guid userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, banned, muted, expires_at, reason, updated_at FROM moderation WHERE user_id = $u;";
        cmd.Parameters.AddWithValue("$u", userId.ToString("N"));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        return new ModerationEntry
        {
            UserId = Guid.ParseExact(r.GetString(0), "N"),
            Banned = r.GetInt64(1) != 0,
            Muted = r.GetInt64(2) != 0,
            ExpiresAt = r.GetInt64(3),
            Reason = r.GetString(4),
            UpdatedAt = r.GetInt64(5)
        };
    }

    public List<ModerationEntry> ListModeration()
    {
        var list = new List<ModerationEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, banned, muted, expires_at, reason, updated_at FROM moderation;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ModerationEntry
            {
                UserId = Guid.ParseExact(r.GetString(0), "N"),
                Banned = r.GetInt64(1) != 0,
                Muted = r.GetInt64(2) != 0,
                ExpiresAt = r.GetInt64(3),
                Reason = r.GetString(4),
                UpdatedAt = r.GetInt64(5)
            });
        }

        return list;
    }

    public void ClearModeration(Guid userId)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM moderation WHERE user_id = $u;";
            cmd.Parameters.AddWithValue("$u", userId.ToString("N"));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
