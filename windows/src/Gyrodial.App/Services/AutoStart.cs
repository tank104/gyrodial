using Microsoft.Win32;

namespace Gyrodial.App.Services;

/// <summary>"Start with Windows": the per-user Run key, launching hidden in the tray.</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Gyrodial";
    const string OldValueName = "Puck";  // before 0.3.0, when the app was called Puck

    /// <summary>
    /// If the old app (Puck) was set to start with Windows, removes its Run value, and registers this
    /// app instead when the (migrated) setting is on. Returns true if it changed anything.
    /// </summary>
    public static bool MigrateOldName(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldValueName) is not string old || !old.Contains("Puck.exe", StringComparison.OrdinalIgnoreCase)) return false;
            key.DeleteValue(OldValueName, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            AppLog.Instance.Warn("app", $"Start with Windows: {e.Message}");
            return false;
        }
        if (enabled) Apply(true);
        return true;
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
            {
                var command = $"\"{exe}\" --tray";
                if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command);
            }
            else if (!enabled && key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e)
        {
            AppLog.Instance.Warn("app", $"Start with Windows: {e.Message}");
        }
    }
}
