using System.Security.Cryptography;
using System.Text.Json;

namespace Puck.Core;

/// <summary>Where the app keeps its files.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Puck");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    /// <summary>Downloaded tools (esptool) live here.</summary>
    public static string ToolsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Puck", "tools");
}

/// <summary>Loads and saves <see cref="PuckSettings"/>. Thread-safe.</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly object _gate = new();
    readonly string _path;
    PuckSettings _current;

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
    public PuckSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Raised (on the thread that called Update) after a change is saved.</summary>
    public event EventHandler? Changed;

    public void Update(Action<PuckSettings> change)
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

    static PuckSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PuckSettings>(File.ReadAllText(path), Json) ?? new PuckSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new PuckSettings();
    }
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
    public static string? ClaudeApiKey(PuckSettings s) =>
        Unprotect(s.ClaudeApiKeyProtected) is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
}
