using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Gyrodial.App.Firmware;

public sealed record FlashProgress(double Fraction, string Status);

/// <summary>
/// Flashes the bundled firmware with esptool. The ESP32-S3's USB-Serial-JTAG port lets esptool
/// reset the chip into its ROM bootloader by itself (no button needed) as long as nothing else holds
/// the port, which is why the app lets go of it first.
/// </summary>
public static partial class FirmwareInstaller
{
    [GeneratedRegex(@"Writing at 0x([0-9a-fA-F]+)")]
    private static partial Regex WritingAt();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%")]
    private static partial Regex Percent();

    /// <summary>Runs the whole install; returns true on success. <paramref name="log"/> gets every esptool line.</summary>
    public static async Task<bool> InstallAsync(EsptoolCommand esptool, FirmwareBundle bundle, string port, bool eraseFirst,
        IProgress<FlashProgress> progress, Action<string> log, CancellationToken ct)
    {
        string Opt(string kebab) => esptool.Kebab ? kebab : kebab.Replace('-', '_');

        string[] common = ["--chip", "esp32s3", "--port", port, "--baud", "921600",
            "--before", Opt("default-reset"), "--after", Opt("hard-reset")];

        if (eraseFirst)
        {
            progress.Report(new FlashProgress(0, "Erasing the flash…"));
            if (await RunAsync(esptool, [.. common, Opt("erase-flash")], null, log, ct) != 0) return false;
        }

        progress.Report(new FlashProgress(0, "Connecting to the dial…"));
        // "keep" spelled out: esptool's default is keep too, but an ESPTOOL_FM variable or an
        // esptool.cfg can change it, and this board's bootloader must keep its DIO header (forcing
        // QIO into it makes the ROM loader fail on the 1.75C's flash chip: a boot loop).
        string flag(string name) => esptool.Kebab ? $"--flash-{name}" : $"--flash_{name}";
        var args = new List<string>(common) { Opt("write-flash"), flag("mode"), "keep", flag("freq"), "keep", flag("size"), "keep", "-z" };
        foreach (var part in bundle.Parts)
        {
            args.Add($"0x{part.Offset:x}");
            args.Add(part.Path);
        }

        long total = Math.Max(1, bundle.TotalBytes);
        FirmwarePart? current = null;
        void OnLine(string line)
        {
            var at = WritingAt().Match(line);
            if (at.Success)
            {
                long addr = long.Parse(at.Groups[1].Value, NumberStyles.HexNumber);
                current = bundle.Parts.LastOrDefault(p => p.Offset <= addr) ?? current;
            }
            var pct = Percent().Match(line);
            if (current is not null && pct.Success && double.TryParse(pct.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            {
                long before = bundle.Parts.TakeWhile(x => x != current).Sum(x => x.Size);
                double fraction = (before + current.Size * Math.Clamp(p / 100, 0, 1)) / total;
                progress.Report(new FlashProgress(fraction, $"Writing {System.IO.Path.GetFileName(current.Path)}… {Math.Round(fraction * 100)} %"));
            }
            else if (line.Contains("Chip is", StringComparison.OrdinalIgnoreCase) || line.Contains("Connected to", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new FlashProgress(0, "Writing…"));
            }
        }

        if (await RunAsync(esptool, args, OnLine, log, ct) != 0) return false;
        progress.Report(new FlashProgress(1, "Done. The dial is restarting."));
        return true;
    }

    static async Task<int> RunAsync(EsptoolCommand esptool, IEnumerable<string> args, Action<string>? onLine, Action<string> log, CancellationToken ct)
    {
        var psi = Esptool.Start(esptool, args);
        log($"> {esptool.FileName} {string.Join(' ', psi.ArgumentList)}");
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Handle(string? data)
        {
            if (string.IsNullOrEmpty(data)) return;
            // esptool redraws its progress bar with \r: keep the last frame of each line
            var line = data.Split('\r', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (string.IsNullOrEmpty(line)) return;
            log(line);
            onLine?.Invoke(line);
        }
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        log($"esptool exited with {p.ExitCode}");
        return p.ExitCode;
    }
}
