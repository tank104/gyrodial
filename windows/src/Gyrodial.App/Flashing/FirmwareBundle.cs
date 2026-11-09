using System.IO;
using System.Text.Json;

namespace Gyrodial.App.Firmware;

public sealed record FirmwarePart(string Path, int Offset, long Size);

/// <summary>
/// The firmware shipped next to Gyrodial.exe in firmware\: the same files and ESP Web Tools manifest as
/// the web installer (ESP-IDF's images: bootloader at 0x0, partition table at 0x8000, OTA data at
/// 0xe000, the app at 0x10000).
/// </summary>
public sealed class FirmwareBundle
{
    public string Version { get; }
    public IReadOnlyList<FirmwarePart> Parts { get; }
    public long TotalBytes => Parts.Sum(p => p.Size);

    FirmwareBundle(string version, IReadOnlyList<FirmwarePart> parts)
    {
        Version = version;
        Parts = parts;
    }

    public static string DefaultFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "firmware");

    /// <summary>The bundle in <paramref name="folder"/>, or null when there is none.</summary>
    public static FirmwareBundle? Load(string? folder = null)
    {
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder)) return null;

        var manifest = System.IO.Path.Combine(folder, "manifest.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = doc.RootElement;
                var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var parts = new List<FirmwarePart>();
                foreach (var part in root.GetProperty("builds")[0].GetProperty("parts").EnumerateArray())
                {
                    var path = System.IO.Path.Combine(folder, part.GetProperty("path").GetString() ?? "");
                    var offsetEl = part.GetProperty("offset");
                    int offset = offsetEl.ValueKind == JsonValueKind.String
                        ? Convert.ToInt32(offsetEl.GetString(), offsetEl.GetString()!.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10)
                        : offsetEl.GetInt32();
                    if (!File.Exists(path)) return null;
                    parts.Add(new FirmwarePart(path, offset, new FileInfo(path).Length));
                }
                return parts.Count > 0 ? new FirmwareBundle(version, parts) : null;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException or IOException)
            {
                AppLog.Instance.Warn("firmware", $"manifest.json: {e.Message}");
            }
        }

        // No manifest: ESP-IDF's file names at the offsets in firmware/partitions.csv
        (string File, int Offset)[] layout = [("bootloader.bin", 0x0), ("partition-table.bin", 0x8000), ("ota_data_initial.bin", 0xE000), ("gyrodial.bin", 0x10000)];
        var found = new List<FirmwarePart>();
        foreach (var (file, offset) in layout)
        {
            var path = System.IO.Path.Combine(folder, file);
            if (!File.Exists(path)) return null;
            found.Add(new FirmwarePart(path, offset, new FileInfo(path).Length));
        }
        return new FirmwareBundle("", found);
    }
}
