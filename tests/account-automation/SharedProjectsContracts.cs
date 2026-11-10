using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAccountDesk;

internal static class SharedProjectsContracts
{
    internal static void Run(Action<string, bool> check)
    {
        const string host = @"local:C:\Fictional\.codex";
        var original = JsonNode.Parse("""
        {
          "electron-account-renderer-state-v1":{"owner":"source","inactive":{
            "target":{"atoms":{"untouched":true},"globals":{
              "local-projects":{"keep":{"id":"keep","name":"Destination","rootPaths":["C:/target"],"chatGptBacking":{"private":true}}},
              "project-order":["keep"],
              "projectless-thread-ids":["projectless"],
              "thread-project-assignments":{"existing":{"projectKind":"chatgpt","projectId":"cloud"}}
            }},
            "other":{"atoms":{},"globals":{"unrelated":true}}
          }},
          "local-projects":{},
          "thread-project-assignments":{},
          "thread-workspace-root-hints":{"new":"C:/new"}
        }
        """)!.AsObject();
        original["app-server-projects-migration-by-host"] = new JsonObject {
            [host] = JsonNode.Parse("""{"projectsMigrated":true,"threadAssignmentsMigrated":true,"threadAssignmentsReadMigrated":true,"pendingThreadAssignmentIds":["pending"]}""")
        };
        var saved = JsonNode.Parse("""
        {
          "electron-account-renderer-state-v1":{"owner":"previous","inactive":{
            "source":{"atoms":{"unrelated":true},"globals":{
              "local-projects":{
                "shared":{"id":"shared","name":"Shared","rootPaths":["C:/fictional"],"chatGptBacking":{"private":true}},
                "deleted":{"id":"deleted","name":"Deleted","rootPaths":["C:/deleted"]}
              },
              "project-order":["deleted","shared"],
              "thread-project-assignments":{
                "local":{"projectKind":"local","projectId":"shared","projectOrigin":"private"},
                "existing":{"projectKind":"local","projectId":"shared"},
                "projectless":{"projectKind":"local","projectId":"shared"},
                "cloud":{"projectKind":"chatgpt","projectId":"shared"},
                "remote":{"projectKind":"local","projectId":"shared"},
                "gone":{"projectKind":"local","projectId":"deleted"}
              },
              "thread-project-membership-host-ids":{"local":"local","remote":"other-host"},
              "thread-workspace-root-hints":{"local":"C:/fictional"}
            }}
          }}
        }
        """)!.AsObject();
        var savedProfile = saved["electron-account-renderer-state-v1"]!["inactive"]!["source"]!["globals"]!;
        savedProfile["app-server-project-id-by-legacy-project-id-by-host"] = new JsonObject {
            [host] = JsonNode.Parse("""{"shared":"live","deleted":"old"}"""),
            ["local:other-installation"] = JsonNode.Parse("""{"shared":"other"}""")
        };
        var live = new HashSet<string> { "live" };
        var result = SharedLocalProjects.Build(original, "source", "target", host, saved, live);
        var target = result["electron-account-renderer-state-v1"]!["inactive"]!["target"]!["globals"]!;
        check("An empty active project profile is recovered from its own verified backup", result["local-projects"]!["shared"] != null);
        check("The next account receives local projects without replacing its own", target["local-projects"]!["shared"] != null &&
            target["local-projects"]!["keep"]!["chatGptBacking"] != null && target["project-order"]!.ToJsonString() == """["keep","shared"]""");
        check("Recovery preserves legacy IDs and maps only the current host", target["app-server-project-id-by-legacy-project-id-by-host"]![host]!["shared"]!.GetValue<string>() == "live" &&
            target["app-server-project-id-by-legacy-project-id-by-host"]!["local:other-installation"] == null);
        check("Deleted projects and their thread links are excluded", result["local-projects"]!["deleted"] == null &&
            target["thread-project-assignments"]!["gone"] == null);
        check("Only local project and thread fields are transferred", target["local-projects"]!["shared"]!["chatGptBacking"] == null &&
            target["thread-project-assignments"]!["local"]!["projectOrigin"] == null &&
            target["thread-project-assignments"]!["cloud"] == null && target["thread-project-assignments"]!["remote"] == null);
        check("Existing target assignments and explicit projectless state win", target["thread-project-assignments"]!["projectless"] == null &&
            target["thread-project-assignments"]!["existing"]!["projectId"]!.GetValue<string>() == "cloud");
        check("Recovered assignments are queued without discarding existing migration work",
            result["app-server-projects-migration-by-host"]![host]!["threadAssignmentsMigrated"]!.GetValue<bool>() == false &&
            result["app-server-projects-migration-by-host"]![host]!["pendingThreadAssignmentIds"]!.AsArray().Any(id => id!.GetValue<string>() == "local") &&
            result["app-server-projects-migration-by-host"]![host]!["pendingThreadAssignmentIds"]!.AsArray().Any(id => id!.GetValue<string>() == "pending"));
        check("Other profiles and original input remain unchanged",
            JsonNode.DeepEquals(result["electron-account-renderer-state-v1"]!["inactive"]!["other"],
                original["electron-account-renderer-state-v1"]!["inactive"]!["other"]) &&
            original["local-projects"]!.AsObject().Count == 0);
        check("Applying the same transition again is idempotent", JsonNode.DeepEquals(result, SharedLocalProjects.Build(result, "source", "target", host, saved, live)));
        check("Unknown catalog and renderer-owner mismatch skip recovery",
            JsonNode.DeepEquals(original, SharedLocalProjects.Build(original, "source", "target", host, saved)) &&
            JsonNode.DeepEquals(original, SharedLocalProjects.Build(original, "wrong", "target", host, saved, live)));
        var newAccount = SharedLocalProjects.Build(result, "source", "new", host);
        check("A later switch uses the restored active state for a new account",
            newAccount["electron-account-renderer-state-v1"]!["inactive"]!["new"]!["globals"]!["local-projects"]!["shared"] != null);
        var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"https://api.openai.com/auth":{"chatgpt_user_id":"fictional"}}""")).TrimEnd('=');
        var auth = JsonSerializer.Serialize(new { tokens = new { account_id = "test", access_token = "bad", id_token = "x." + token + ".x" } });
        check("Desktop owner derives from the workspace and a valid user claim", SharedLocalProjects.OwnerFromAuth(auth) ==
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("""["fictional","test"]"""u8)).ToLowerInvariant());
    }
}
