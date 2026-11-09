using System.Security.Cryptography;
using System.Text.Json;

namespace Gyrodial.Core;

/// <summary>Where the app keeps its files.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gyrodial");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    /// <summary>Downloaded tools (esptool) live here.</summary>
    public static string ToolsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gyrodial", "tools");

    /// <summary>Where versions before 0.3.0 (when the app was called Puck) kept their files.</summary>
    public static string OldDataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Puck");

    /// <summary>
    /// Moves the files of the app's old name (settings with the pairing, the Claude Code hook key,
    /// logs) to <see cref="DataDir"/>, once: only while DataDir doesn't exist yet. Returns what it
    /// did, for the log, or null if there was nothing to move.
    /// </summary>
    public static string? MigrateOldDataDir() => MigrateDataDir(OldDataDir, DataDir);

    public static string? MigrateDataDir(string from, string to)
    {
        if (!Directory.Exists(from) || Directory.Exists(to)) return null;
        try
        {
            Directory.Move(from, to);
            return $"Moved {from} to {to}";
        }
        catch (IOException)
        {
            // Something still has a file open there (an old copy still running?): copy instead
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(file, target, overwrite: false); } catch (IOException) { }
            }
            return $"Copied {from} to {to}";
        }
    }
}

/// <summary>Loads and saves <see cref="GyrodialSettings"/>. Thread-safe.</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly object _gate = new();
    readonly string _path;
    GyrodialSettings _current;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _current = Load(_path);
        if (string.IsNullOrEmpty(_current.HostId))
        {
            _current.HostId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            Save();
        }
    }

    /// <summary>The settings. Treat as read-only; change them with <see cref="Update"/>.</summary>
    public GyrodialSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Raised (on the thread that called Update) after a change is saved.</summary>
    public event EventHandler? Changed;

    public void Update(Action<GyrodialSettings> change)
    {
        lock (_gate)
        {
            change(_current);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_current, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static GyrodialSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return Parse(File.ReadAllText(path));
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new GyrodialSettings();
    }

    /// <summary>Reads a settings file, including one written before 0.3.0 (when the app was called Puck).</summary>
    public static GyrodialSettings Parse(string json)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: new() { AllowTrailingCommas = true }) is not System.Text.Json.Nodes.JsonObject o)
            return new GyrodialSettings();
        // The paired dials were saved as "Pucks"
        if (o.ContainsKey("Pucks") && !o.ContainsKey(nameof(GyrodialSettings.Dials)))
        {
            var dials = o["Pucks"];
            o.Remove("Pucks");
            o[nameof(GyrodialSettings.Dials)] = dials;
        }
        var settings = o.Deserialize<GyrodialSettings>(Json) ?? new GyrodialSettings();
        // The old default system prompt named the app: give it the new default (an edited one stays)
        if (settings.ClaudeSystemPrompt == OldDefaultSystemPrompt) settings.ClaudeSystemPrompt = new GyrodialSettings().ClaudeSystemPrompt;
        return settings;
    }

    const string OldDefaultSystemPrompt =
        "You are Claude, answering through Puck: a small round screen on the user's desk. They speak to you and " +
        "read your reply on a 1.75-inch round display, so keep answers short: one to three plain sentences, no " +
        "markdown, no lists, no code blocks unless they ask for code.";
}

/// <summary>DPAPI (current Windows user) for the secrets in settings.</summary>
public static class Secrets
{
    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The Claude API key: from settings, else the ANTHROPIC_API_KEY environment variable.</summary>
    public static string? ClaudeApiKey(GyrodialSettings s) =>
        Unprotect(s.ClaudeApiKeyProtected) is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
}
