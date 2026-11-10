using System.Collections.Generic;
using System.IO;

namespace CodexAccountDesk;

internal static class LocalProjectCatalog
{
    internal static IReadOnlySet<string> ReadIds(string home)
    {
        var result = new HashSet<string>();
        var path = Path.Combine(home, "state_5.sqlite");
        if (!File.Exists(path)) return result;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The local project database is a link.");
        using var database = new ReadOnlySqlite(path);
        if (database.Read("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'projects'").Count != 1)
            return result;
        foreach (var row in database.Read("SELECT id FROM projects"))
            if (row[0] is { } id) result.Add(id);
        return result;
    }
}
