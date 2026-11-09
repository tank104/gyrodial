// puck-hook.exe: the Claude Code hook command for Puck.
//
//   puck-hook permission   (PermissionRequest hook) asks the puck; prints allow/deny, or nothing
//   puck-hook notify       (Notification hook) shows Claude Code's notification on the puck
// (The hook's own "hook_event_name" wins over the argument when it's there.)
//
// Claude Code passes the hook's JSON on stdin. This forwards it to the Puck app on
// http://127.0.0.1:<ClaudeCodePort> and waits. Whatever goes wrong (Puck not running, puck not
// connected, a timeout) it prints nothing and exits 0, so Claude Code simply asks in the terminal.
//
// Only Puck's answer counts: the request carries an HMAC made with the key Puck keeps in
// %AppData%\Puck\hook.key (this Windows user only), and the answer must carry one too, so a program
// that grabbed the port first, or a proxy, can't approve anything. No proxy is ever used.

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int DefaultPort = 47825;
const int DefaultTimeoutSeconds = 110;

const string AllowJson = """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""";
const string DenyJson = """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"Denied from Puck"}}}""";

try
{
    string input;
    using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
        input = reader.ReadToEnd();

    var notify = Mode(input, args.Length > 0 ? args[0] : "permission") == "notify";
    var key = ReadKey();
    if (key is null) return 0;  // Puck never ran (or its key is gone): nobody to trust
    var (port, timeoutSeconds) = ReadSettings();

    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(notify ? 5 : timeoutSeconds + 5) };
    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/{(notify ? "notify" : "permission")}")
    {
        Content = new StringContent(input, Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("X-Puck-Nonce", nonce);
    request.Headers.Add("X-Puck-Auth", Mac(key, "request|" + nonce));
    using var response = http.Send(request);
    if (notify || !response.IsSuccessStatusCode) return 0;

    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;
    var decision = root.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
    var mac = root.TryGetProperty("mac", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
    if (!Verify(key, $"response|{nonce}|{decision ?? "none"}", mac)) return 0;  // not Puck's answer
    var output = decision switch { "allow" => AllowJson, "deny" => DenyJson, _ => null };
    if (output is not null)
    {
        using var stdout = Console.OpenStandardOutput();
        var bytes = Encoding.UTF8.GetBytes(output + "\n");
        stdout.Write(bytes, 0, bytes.Length);
    }
}
catch
{
    // Never get in Claude Code's way: no output means "no decision".
}
return 0;

// "notify" or "permission": from the hook's JSON when it says, else from the argument
static string Mode(string input, string arg)
{
    try
    {
        using var doc = JsonDocument.Parse(input);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("hook_event_name", out var e) && e.ValueKind == JsonValueKind.String)
        {
            if (e.GetString() == "Notification") return "notify";
            if (e.GetString() == "PermissionRequest") return "permission";
        }
    }
    catch (JsonException) { }
    return arg.Trim().ToLowerInvariant() == "notify" ? "notify" : "permission";
}

// Puck's shared key (%AppData%\Puck\hook.key, 64 hex chars), or null. PUCK_HOOK_KEY_FILE points
// elsewhere (for tests).
static byte[]? ReadKey()
{
    try
    {
        var path = Environment.GetEnvironmentVariable("PUCK_HOOK_KEY_FILE");
        if (string.IsNullOrEmpty(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Puck", "hook.key");
        if (!File.Exists(path)) return null;
        var key = Convert.FromHexString(File.ReadAllText(path).Trim());
        return key.Length == 32 ? key : null;
    }
    catch
    {
        return null;
    }
}

static string Mac(byte[] key, string message) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

static bool Verify(byte[] key, string message, string? mac)
{
    if (string.IsNullOrEmpty(mac) || mac.Length != 64) return false;
    try
    {
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(mac), HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
    }
    catch (FormatException)
    {
        return false;
    }
}

// The port and timeout from Puck's settings (%AppData%\Puck\settings.json), or the defaults.
// PUCK_HOOK_PORT overrides the port (for tests, or a second copy of Puck).
static (int Port, int TimeoutSeconds) ReadSettings()
{
    var port = DefaultPort;
    var timeout = DefaultTimeoutSeconds;
    try
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("PUCK_HOOK_PORT"), out var envPort) && envPort is > 0 and < 65536)
            port = envPort;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Puck", "settings.json");
        if (!File.Exists(path)) return (port, timeout);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (port == DefaultPort && doc.RootElement.TryGetProperty("ClaudeCodePort", out var p) && p.TryGetInt32(out var pv) && pv is > 0 and < 65536) port = pv;
        if (doc.RootElement.TryGetProperty("ClaudeCodeTimeoutSeconds", out var t) && t.TryGetInt32(out var tv) && tv > 0) timeout = tv;
    }
    catch
    {
        // defaults
    }
    return (port, timeout);
}
