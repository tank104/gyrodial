using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Puck.Core;

namespace Puck.Integrations.ClaudeCode;

/// <summary>
/// The secret puck-hook.exe and the app share (%AppData%\Puck\hook.key, readable by this Windows
/// user only). Without it, anything that got to 127.0.0.1:&lt;port&gt; first (another user's
/// process, a proxy) could answer the hook and approve every Claude Code request:
///   hook → app:  X-Puck-Nonce: n, X-Puck-Auth: HMAC(key, "request|" + n)
///   app → hook:  {"decision": d, "mac": HMAC(key, "response|" + n + "|" + (d ?? "none"))}
/// The hook only prints a decision whose mac checks out. puck-hook/Program.cs does the same sums.
/// </summary>
public sealed class HookKey
{
    public const string NonceHeader = "X-Puck-Nonce";
    public const string AuthHeader = "X-Puck-Auth";
    const int KeyBytes = 32;

    readonly object _gate = new();
    byte[]? _key;

    public static string DefaultPath => Path.Combine(AppPaths.DataDir, "hook.key");

    public string FilePath { get; }

    public HookKey(string? path = null) => FilePath = path ?? DefaultPath;

    /// <summary>The key, made (and written for this Windows user only) if there isn't a good one.</summary>
    public byte[] Ensure()
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            _key = TryRead(FilePath) ?? Create();
            return _key;
        }
    }

    static byte[]? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var key = Convert.FromHexString(File.ReadAllText(path).Trim());
            return key.Length == KeyBytes ? key : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    byte[] Create()
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Only this user, nothing inherited from the folder
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));

        var tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
        {
            var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(key));
            stream.Write(hex);
        }
        File.Move(tmp, FilePath, overwrite: true);  // a move keeps the file's own ACL
        return key;
    }

    public static string Mac(byte[] key, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

    /// <summary>Constant-time check of a hex MAC.</summary>
    public static bool Verify(byte[] key, string message, string? mac)
    {
        if (string.IsNullOrEmpty(mac) || mac.Length != 64) return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(mac);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(given, HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
    }

    public static string RequestMessage(string nonce) => "request|" + nonce;

    public static string ResponseMessage(string nonce, string? decision) => $"response|{nonce}|{decision ?? "none"}";
}
