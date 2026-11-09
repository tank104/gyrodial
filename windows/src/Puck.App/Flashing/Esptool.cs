using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Puck.Core;

namespace Puck.App.Firmware;

/// <summary>A way to run esptool: an exe, or Python with the esptool module.</summary>
public sealed record EsptoolCommand(string FileName, IReadOnlyList<string> PrefixArgs, string Description)
{
    /// <summary>esptool 5 renamed commands and options to kebab-case (write-flash, --flash-mode...).</summary>
    public bool Kebab { get; init; }
}

/// <summary>
/// Finds esptool: bundled with the app, downloaded earlier, downloaded now from Espressif's GitHub
/// releases (the standalone Windows build, no Python needed), or Python's esptool module.
/// </summary>
public static class Esptool
{
    // The download is pinned to one release and checked against its SHA-256 (from the GitHub
    // release's asset digest): what runs against the user's board is exactly what was tested.
    const string PinnedTag = "v5.4.0";
    const string PinnedUrl = "https://github.com/espressif/esptool/releases/download/v5.4.0/esptool-v5.4.0-windows-amd64.zip";
    const string PinnedSha256 = "b7f6b9dd301a210b31f4829118c909c84aae23107f9ca1fdc14ccf4d7384be2e";

    public static async Task<EsptoolCommand?> FindAsync(Action<string> status, CancellationToken ct)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "esptool.exe");
        if (File.Exists(bundled)) return await WithVersionAsync(new EsptoolCommand(bundled, [], "bundled esptool"), ct);

        var downloaded = FindDownloaded();
        if (downloaded is not null) return await WithVersionAsync(new EsptoolCommand(downloaded, [], "esptool"), ct);

        try
        {
            status("Downloading esptool from Espressif…");
            var exe = await DownloadAsync(ct);
            if (exe is not null) return await WithVersionAsync(new EsptoolCommand(exe, [], "esptool"), ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException)
        {
            AppLog.Instance.Warn("firmware", $"esptool download failed: {e.Message}");
        }

        foreach (var python in new[] { "py", "python", "python3" })
        {
            var cmd = new EsptoolCommand(python, ["-m", "esptool"], $"{python} -m esptool");
            if (await VersionAsync(cmd, ct) is not null) return await WithVersionAsync(cmd, ct);
        }
        return null;
    }

    /// <summary>The pinned esptool, if it was downloaded before (older unpinned downloads are ignored).</summary>
    static string? FindDownloaded()
    {
        var dir = Path.Combine(AppPaths.ToolsDir, $"esptool-{PinnedTag}");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "esptool.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    static async Task<string?> DownloadAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Puck/{Transport.ConnectionManager.AppVersion}");

        var target = Path.Combine(AppPaths.ToolsDir, $"esptool-{PinnedTag}");
        var zip = Path.Combine(Path.GetTempPath(), $"puck-esptool-{PinnedTag}-{Environment.ProcessId}.zip");
        try
        {
            await using (var src = await http.GetStreamAsync(PinnedUrl, ct))
            await using (var dst = File.Create(zip))
                await src.CopyToAsync(dst, ct);

            string sha;
            await using (var f = File.OpenRead(zip))
                sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(f, ct));
            if (!string.Equals(sha, PinnedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"esptool download doesn't match its checksum (got {sha[..12]}…)");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            ZipFile.ExtractToDirectory(zip, target);
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
        AppLog.Instance.Info("firmware", $"Downloaded esptool {PinnedTag} (checksum verified)");
        return Directory.EnumerateFiles(target, "esptool.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    static async Task<EsptoolCommand?> WithVersionAsync(EsptoolCommand cmd, CancellationToken ct)
    {
        var version = await VersionAsync(cmd, ct);
        var kebab = version is not null && System.Text.RegularExpressions.Regex.Match(version, @"v?(\d+)\.") is { Success: true } m
            && int.Parse(m.Groups[1].Value) >= 5;
        return cmd with { Kebab = kebab, Description = version is null ? cmd.Description : $"{cmd.Description} {version}" };
    }

    /// <summary>"v4.8.1", or null when the command doesn't run.</summary>
    static async Task<string?> VersionAsync(EsptoolCommand cmd, CancellationToken ct)
    {
        try
        {
            var psi = Start(cmd, ["version"]);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(output, @"v?\d+\.\d+(\.\d+)?");
            return m.Success ? (m.Value.StartsWith('v') ? m.Value : "v" + m.Value) : "";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static ProcessStartInfo Start(EsptoolCommand cmd, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(cmd.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in cmd.PrefixArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return psi;
    }
}
