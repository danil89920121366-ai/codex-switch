using System;
using System.IO;
using System.Text.Json;

namespace CodexAccountDesk;

internal sealed class QuotaTurnHistory : IDisposable
{
    private readonly ReadOnlySqlite _database;
    private QuotaTurnHistory(string path) => _database = new ReadOnlySqlite(path);

    internal static QuotaTurnHistory? Open(string home)
    {
        var path = Path.Combine(home, "thread_history_1.sqlite");
        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? new(path) : null;
    }

    internal string? RecentFailure(string threadId, DateTimeOffset now)
    {
        var failure = ReadLatestFailure(threadId);
        if (failure == null) return null;
        var age = now.ToUnixTimeSeconds() - failure.Value.CompletedAt;
        return age is >= 0 and <= 120 ? failure.Value.TurnId : null;
    }

    internal bool IsLatestFailure(string threadId, string turnId) => ReadLatestFailure(threadId)?.TurnId == turnId;

    private (string TurnId, long CompletedAt)? ReadLatestFailure(string threadId)
    {
        var rows = _database.Read("""
            SELECT turn_id, status, completed_at, error_json
            FROM thread_turns WHERE thread_id = ? ORDER BY rollout_ordinal DESC LIMIT 1
            """, threadId);
        if (rows.Count == 0) return null;
        var row = rows[0];
        if (row[0] is not { Length: > 0 } id || row[1] != "failed" || row[3] is not { } error ||
            !long.TryParse(row[2], out var completedAt)) return null;
        using var parsed = JsonDocument.Parse(error);
        return parsed.RootElement.ValueKind == JsonValueKind.Object &&
            JsonValue.String(parsed.RootElement, "codexErrorInfo") == "usageLimitExceeded" ? (id, completedAt) : null;
    }

    public void Dispose() => _database.Dispose();
}
