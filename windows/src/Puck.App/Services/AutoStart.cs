using Microsoft.Win32;

namespace Puck.App.Services;

/// <summary>"Start with Windows": the per-user Run key, launching hidden in the tray.</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Puck";

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
