using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using JValue = System.Text.Json.Nodes.JsonValue;

namespace CodexAccountDesk;

internal static class SharedLocalProjects
{
    private const string Scope = "electron-account-renderer-state-v1";
    private const string Projects = "local-projects";
    private const string Mappings = "app-server-project-id-by-legacy-project-id-by-host";
    private const string Migration = "app-server-projects-migration-by-host";
    private const string Assignments = "thread-project-assignments";
    private const string Membership = "thread-project-membership-host-ids";
    private const string Hints = "thread-workspace-root-hints";
    private const string BackupFolder = "project-sharing-backups";
    private static readonly string[] ProjectFields = { "id", "name", "rootPaths", "createdAt", "updatedAt" };

    internal static string? OwnerFromAuth(string authJson)
    {
        if (JsonNode.Parse(authJson)?["tokens"] is not JsonObject tokens ||
            Text(tokens["account_id"]) is not { Length: > 0 } account) return null;
        foreach (var key in new[] { "access_token", "id_token" })
        {
            var parts = Text(tokens[key])?.Split('.');
            if (parts?.Length != 3) continue;
            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                var claims = JsonNode.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                if (Text(claims?["https://api.openai.com/auth"]?["chatgpt_user_id"]) is { Length: > 0 } user)
                    return SecureFiles.Hash(JsonSerializer.Serialize(new[] { user, account })).ToLowerInvariant();
            }
            catch (FormatException) { }
            catch (JsonException) { }
        }
        return null;
    }

    private static string? Text(JsonNode? node) => node is JValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static JsonObject? Object(JsonObject source, string key) => source[key] as JsonObject;
    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonObject? Profile(JsonObject snapshot, string owner)
    {
        if (snapshot[Scope] is not JsonObject scope) return null;
        return Text(scope["owner"]) == owner ? snapshot : scope["inactive"]?[owner]?["globals"] as JsonObject;
    }

    private static bool IsProject(string id, JsonNode? value) =>
        value is JsonObject project && Text(project["id"]) == id && Text(project["name"]) is { Length: > 0 } &&
        project["rootPaths"] is JsonArray roots && roots.Count > 0 && roots.All(root => !string.IsNullOrWhiteSpace(Text(root)));

    internal static JsonObject Build(JsonObject original, string sourceOwner, string targetOwner, string host,
        JsonObject? backup = null, IReadOnlySet<string>? catalog = null)
    {
        var result = original.DeepClone().AsObject();
        if (result[Scope] is not JsonObject scope || Text(scope["owner"]) != sourceOwner ||
            scope["inactive"] is not JsonObject inactive) return result;
        var hasProjects = Object(original, Projects)?.Any(pair => IsProject(pair.Key, pair.Value)) == true;
        if (!hasProjects && backup != null && catalog is { Count: > 0 } && Profile(backup, sourceOwner) is { } saved)
            Merge(result, saved, host, catalog);
        if (sourceOwner == targetOwner || Object(result, Projects) is not { Count: > 0 }) return result;
        if (inactive[targetOwner] == null)
            inactive[targetOwner] = new JsonObject { ["atoms"] = new JsonObject(), ["globals"] = new JsonObject() };
        if (inactive[targetOwner]?["globals"] is not JsonObject target)
            throw new InvalidDataException("Unsupported destination project profile.");
        Merge(target, result, host, null);
        return result;
    }

    private static void Merge(JsonObject destination, JsonObject source, string host, IReadOnlySet<string>? catalog)
    {
        if (Object(source, Projects) is not { Count: > 0 } projects) return;
        var sourceMap = source[Mappings]?[host] as JsonObject;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, value) in projects)
        {
            if (!IsProject(id, value)) continue;
            var mapped = Text(sourceMap?[id]);
            if (catalog != null && (mapped == null || !catalog.Contains(mapped))) continue;
            ids.Add(id);
            var destinationProjects = EnsureObject(destination, Projects);
            if (!destinationProjects.ContainsKey(id))
            {
                var local = new JsonObject();
                foreach (var field in ProjectFields)
                    if (value![field] is { } entry) local[field] = entry.DeepClone();
                destinationProjects[id] = local;
            }
            if (mapped != null)
            {
                var hostMap = EnsureObject(EnsureObject(destination, Mappings), host);
                if (!hostMap.ContainsKey(id)) hostMap[id] = mapped;
            }
        }
        if (ids.Count == 0) return;

        var order = destination["project-order"] as JsonArray ?? new JsonArray();
        if (order.Parent == null) destination["project-order"] = order;
        var ordered = order.Select(Text).ToHashSet(StringComparer.Ordinal);
        foreach (var id in (source["project-order"] as JsonArray ?? new JsonArray()).Select(Text).Concat(ids))
            if (id != null && ids.Contains(id) && ordered.Add(id)) order.Add(id);

        var projectless = (destination["projectless-thread-ids"] as JsonArray ?? new JsonArray()).Select(Text).ToHashSet(StringComparer.Ordinal);
        if (Object(source, Assignments) is not JsonObject sourceAssignments) return;
        var restored = new List<string>();
        foreach (var (thread, value) in sourceAssignments)
        {
            if (value is not JsonObject assignment || Text(assignment["projectKind"]) != "local" ||
                !ids.Contains(Text(assignment["projectId"]) ?? "") || projectless.Contains(thread) ||
                Object(destination, Assignments)?.ContainsKey(thread) == true) continue;
            var sourceHost = Text(source[Membership]?[thread]);
            var targetHost = Text(destination[Membership]?[thread]);
            if (sourceHost is not (null or "local") || targetHost is not (null or "local")) continue;
            EnsureObject(destination, Assignments)[thread] = new JsonObject { ["projectKind"] = "local", ["projectId"] = Text(assignment["projectId"]) };
            if (sourceHost != null) EnsureObject(destination, Membership)[thread] = sourceHost;
            if (Text(source[Hints]?[thread]) is { } hint && Object(destination, Hints)?.ContainsKey(thread) != true)
                EnsureObject(destination, Hints)[thread] = hint;
            restored.Add(thread);
        }
        if (restored.Count > 0 && destination[Migration]?[host] is JsonObject migration)
        {
            var pending = (migration["pendingThreadAssignmentIds"] as JsonArray ?? new JsonArray()).Select(Text);
            migration["pendingThreadAssignmentIds"] = new JsonArray(pending.Concat(restored)
                .Where(id => id != null).Distinct(StringComparer.Ordinal).Select(id => JValue.Create(id)).ToArray());
            migration["threadAssignmentsMigrated"] = false;
        }
    }

    internal static void Apply(string home, string? sourceAuth, string targetAuth, Func<bool> desktopRunning)
    {
        var optIn = Path.Combine(home, "share-local-projects.json");
        if (sourceAuth == null || !File.Exists(optIn) ||
            JsonNode.Parse(File.ReadAllText(optIn))?["enabled"]?.GetValue<bool>() != true) return;
        var path = Path.Combine(home, ".codex-global-state.json");
        if (!File.Exists(path)) return;
        var source = OwnerFromAuth(sourceAuth);
        var target = OwnerFromAuth(targetAuth);
        if (source == null || target == null) return;
        if (desktopRunning()) throw new IOException("Close Codex before synchronizing local projects.");
        var before = File.ReadAllText(path);
        var parsed = JsonNode.Parse(before)?.AsObject() ?? throw new InvalidDataException("Invalid Codex desktop state.");
        if (Text(parsed[Scope]?["owner"]) != source) return;
        var expectedHost = "local:" + Path.TrimEndingDirectorySeparator(home);
        var host = Object(parsed, Migration)?.Select(pair => pair.Key).FirstOrDefault(key =>
            string.Equals(key, expectedHost, StringComparison.OrdinalIgnoreCase)) ?? expectedHost;
        var backupDirectory = Path.Combine(home, BackupFolder);
        JsonObject? backup = null;
        IReadOnlySet<string>? catalog = null;
        if (Object(parsed, Projects)?.Any(pair => IsProject(pair.Key, pair.Value)) != true && Directory.Exists(backupDirectory))
        {
            SecureFiles.CreatePrivateDirectory(backupDirectory);
            catalog = LocalProjectCatalog.ReadIds(home);
            if (catalog.Count > 0) backup = FindBackup(backupDirectory, source, host, catalog);
        }
        var repaired = Build(parsed, source, target, host, backup, catalog);
        if (JsonNode.DeepEquals(parsed, repaired)) return;
        SecureFiles.CreatePrivateDirectory(backupDirectory);
        var backupName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json";
        SecureFiles.AtomicWriteText(Path.Combine(backupDirectory, backupName), before);
        var recovery = CreateRecoverySnapshot(repaired, source, target, host);
        SecureFiles.AtomicWriteText(Path.Combine(backupDirectory, Path.GetFileNameWithoutExtension(backupName) + ".recovery.json"), recovery.ToJsonString());
        if (desktopRunning() || File.ReadAllText(path) != before)
            throw new IOException("Codex desktop state changed during project recovery. Retry the switch.");
        SecureFiles.AtomicWriteText(path, repaired.ToJsonString());
    }

    private static JsonObject CreateRecoverySnapshot(JsonObject state, string source, string target, string host)
    {
        var snapshot = new JsonObject();
        Merge(snapshot, state, host, null);
        var inactive = new JsonObject();
        if (target != source && Profile(state, target) is { } profile)
        {
            var globals = new JsonObject();
            Merge(globals, profile, host, null);
            inactive[target] = new JsonObject { ["globals"] = globals };
        }
        snapshot[Scope] = new JsonObject { ["owner"] = source, ["inactive"] = inactive };
        return snapshot;
    }

    private static JsonObject? FindBackup(string directory, string owner, string host, IReadOnlySet<string> catalog)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(20))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 64 * 1024 * 1024) continue;
            JsonObject? snapshot;
            try { snapshot = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
            catch (JsonException) { continue; }
            if (snapshot == null || Profile(snapshot, owner) is not { } profile ||
                Object(profile, Projects) is not { } projects || profile[Mappings]?[host] is not JsonObject map) continue;
            if (projects.Any(pair => IsProject(pair.Key, pair.Value) && Text(map[pair.Key]) is { } mapped && catalog.Contains(mapped))) return snapshot;
        }
        return null;
    }
}
