using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gyrodial.Integrations.ClaudeCode;

/// <summary>Claude Code's settings file is one Gyrodial won't rewrite; the message says why and what to do.</summary>
public sealed class HookSettingsException(string message) : Exception(message);

/// <summary>
/// Adds and removes the gyrodial-hook.exe entries in Claude Code's user settings
/// (%CLAUDE_CONFIG_DIR% or %USERPROFILE%\.claude, settings.json), leaving everything else in the file
/// as it was. Our entries are the hook handlers whose command mentions "gyrodial-hook", or
/// "puck-hook" (versions before 0.3.0, when the app was called Puck).
///
/// It only rewrites a file it can reproduce faithfully: one with comments, duplicate keys, or a
/// "hooks" section of an unexpected shape is left alone (with a message saying so), because
/// round-tripping it through a JSON object would drop or change the user's content.
/// </summary>
public sealed class HookInstaller
{
    const string Marker = "gyrodial-hook";
    const string OldMarker = "puck-hook";  // before 0.3.0
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonDocumentOptions Strict = new() { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string SettingsPath { get; }
    public string HookExePath { get; }

    public HookInstaller(string? settingsPath = null, string? hookExePath = null)
    {
        SettingsPath = settingsPath ?? DefaultSettingsPath();
        HookExePath = hookExePath ?? Path.Combine(AppContext.BaseDirectory, "gyrodial-hook.exe");
    }

    static string DefaultSettingsPath()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var dir = string.IsNullOrWhiteSpace(configDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Environment.ExpandEnvironmentVariables(configDir.Trim());
        return Path.Combine(dir, "settings.json");
    }

    public enum HookState { NotInstalled, Installed, Stale, OldTimeout }

    /// <summary>Installed = a PermissionRequest handler runs this app's gyrodial-hook.exe (with
    /// <paramref name="expectedTimeout"/>, when given); Stale = one runs a gyrodial-hook.exe somewhere
    /// else (an old install location), or the old puck-hook.exe; OldTimeout = ours, with another timeout.</summary>
    public HookState GetState(int? expectedTimeout = null)
    {
        try
        {
            var root = Load(forWrite: false);
            var ours = Handlers(root, "PermissionRequest").Where(h => IsOurs(Str(h["command"]) ?? "")).ToList();
            if (ours.Count == 0) return HookState.NotInstalled;
            var here = ours.FirstOrDefault(h => SamePath(Str(h["command"]) ?? "", HookExePath));
            if (here is null) return HookState.Stale;
            if (expectedTimeout is int t && !(here["timeout"] is JsonValue v && v.TryGetValue(out int got) && got == t))
                return HookState.OldTimeout;
            return HookState.Installed;
        }
        catch (Exception)
        {
            // unreadable (or a shape we don't understand): as far as Gyrodial can tell, not installed
            return HookState.NotInstalled;
        }
    }

    /// <summary>Adds (or refreshes) the PermissionRequest and Notification entries. Returns the backup's path, if one was made.</summary>
    public string? Install(int timeoutSeconds)
    {
        var root = Load(forWrite: true);
        var hooks = HooksSection(root, create: true)!;
        RemoveOurs(hooks, "PermissionRequest");
        RemoveOurs(hooks, "Notification");
        AddGroup(hooks, "PermissionRequest", "*", "permission", timeoutSeconds);
        AddGroup(hooks, "Notification", null, "notify", 15);
        var backup = Backup();
        Save(root);
        return backup;
    }

    /// <summary>Removes our entries. Returns the backup's path, if anything changed.</summary>
    public string? Remove()
    {
        if (!File.Exists(SettingsPath)) return null;
        var root = Load(forWrite: true);
        if (HooksSection(root, create: false) is not { } hooks) return null;
        var changed = RemoveOurs(hooks, "PermissionRequest") | RemoveOurs(hooks, "Notification");
        if (!changed) return null;
        if (hooks.Count == 0) root.Remove("hooks");
        var backup = Backup();
        Save(root);
        return backup;
    }

    /// <summary>The "hooks" object, checked to be the shape Claude Code uses (or made, if absent).</summary>
    JsonObject? HooksSection(JsonObject root, bool create)
    {
        if (!root.ContainsKey("hooks"))
        {
            if (!create) return null;
            var made = new JsonObject();
            root["hooks"] = made;
            return made;
        }
        if (root["hooks"] is not JsonObject hooks)
            throw Refusal("its \"hooks\" setting isn't an object");
        foreach (var name in (string[])["PermissionRequest", "Notification"])
        {
            if (hooks.ContainsKey(name) && hooks[name] is not JsonArray)
                throw Refusal($"its \"hooks.{name}\" setting isn't a list");
        }
        return hooks;
    }

    void AddGroup(JsonObject hooks, string eventName, string? matcher, string mode, int timeoutSeconds)
    {
        if (hooks[eventName] is not JsonArray groups)
        {
            groups = [];
            hooks[eventName] = groups;
        }
        var group = new JsonObject();
        if (matcher is not null) group["matcher"] = matcher;
        // Exec form (command + args): no shell, so a path with spaces needs no quoting.
        group["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = HookExePath,
            ["args"] = new JsonArray(mode),
            ["timeout"] = timeoutSeconds,
        });
        groups.Add(group);
    }

    /// <summary>Drops our handlers from one event, and any matcher group that ends up empty because of it.</summary>
    static bool RemoveOurs(JsonObject hooks, string eventName)
    {
        if (hooks[eventName] is not JsonArray groups) return false;
        var changed = false;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray handlers) continue;
            var before = handlers.Count;
            for (var j = handlers.Count - 1; j >= 0; j--)
            {
                if (handlers[j] is JsonObject h && IsOurs(Str(h["command"]) ?? "")) handlers.RemoveAt(j);
            }
            if (handlers.Count == before) continue;
            changed = true;
            if (handlers.Count == 0) groups.RemoveAt(i);
        }
        if (changed && groups.Count == 0) hooks.Remove(eventName);
        return changed;
    }

    static IEnumerable<JsonObject> Handlers(JsonObject root, string eventName) =>
        root["hooks"] is JsonObject hooks && hooks[eventName] is JsonArray groups
            ? groups.OfType<JsonObject>().SelectMany(g => g["hooks"] as JsonArray ?? []).OfType<JsonObject>()
            : [];

    static bool IsOurs(string command) =>
        command.Contains(Marker, StringComparison.OrdinalIgnoreCase) || command.Contains(OldMarker, StringComparison.OrdinalIgnoreCase);

    static bool SamePath(string command, string exe) =>
        string.Equals(command.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase);

    HookSettingsException Refusal(string why) =>
        new($"Gyrodial didn't change {SettingsPath}: {why}, and rewriting it could lose some of your settings. " +
            $"Add the hook by hand instead: a \"PermissionRequest\" hook (matcher \"*\") running \"{HookExePath}\" with args [\"permission\"].");

    /// <summary>
    /// The settings as a JSON object. For writing, the file must round-trip: no comments (they'd be
    /// dropped) and no duplicate keys.
    /// </summary>
    JsonObject Load(bool forWrite)
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: forWrite ? Strict : Lenient);
        }
        catch (JsonException) when (forWrite)
        {
            // Readable with comments allowed: then it has comments, which a rewrite would drop
            try
            {
                JsonNode.Parse(text, documentOptions: Lenient);
            }
            catch (Exception)
            {
                throw Refusal("it isn't valid JSON");
            }
            throw Refusal("it has comments");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // JsonObject refuses duplicate property names
            throw Refusal("it has a setting written twice");
        }
        if (node is not JsonObject root)
            throw forWrite ? Refusal("it isn't a JSON object") : new JsonException("Claude Code's settings.json isn't a JSON object.");
        if (forWrite) EnsureNoDuplicates(root);
        return root;
    }

    /// <summary>JsonNode builds objects lazily, so duplicates surface only when a property is
    /// touched: walk the whole tree now, before anything is changed.</summary>
    void EnsureNoDuplicates(JsonNode? node)
    {
        try
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (_, child) in o) EnsureNoDuplicates(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) EnsureNoDuplicates(child);
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw Refusal("it has a setting written twice");
        }
    }

    /// <summary>The real file behind the settings path (Claude Code's settings are sometimes a symlink into a dotfiles repo).</summary>
    string TargetPath()
    {
        try
        {
            var info = new FileInfo(SettingsPath);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return target.FullName;
        }
        catch (IOException) { }
        return SettingsPath;
    }

    string? Backup()
    {
        var source = TargetPath();
        if (!File.Exists(source)) return null;
        var stem = $"{SettingsPath}.gyrodial-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var n = 1; ; n++)
        {
            var backup = n == 1 ? stem : $"{stem}-{n}";
            if (File.Exists(backup)) continue;
            try
            {
                File.Copy(source, backup, overwrite: false);
                return backup;
            }
            catch (IOException) when (File.Exists(backup) && n < 100)
            {
                // made by someone else in the meantime: next name
            }
        }
    }

    void Save(JsonObject root)
    {
        var target = TargetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = $"{target}.gyrodial-tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(tmp, target, overwrite: true);
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
