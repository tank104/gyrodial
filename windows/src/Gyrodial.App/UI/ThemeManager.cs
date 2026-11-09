using System.Windows;
using Microsoft.Win32;

namespace Gyrodial.App.UI;

/// <summary>Follows Windows' light/dark setting: swaps the color dictionary and tells open windows.</summary>
public static class ThemeManager
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static readonly Uri DarkUri = new("pack://application:,,,/UI/Theme/Dark.xaml");
    static readonly Uri LightUri = new("pack://application:,,,/UI/Theme/Light.xaml");
    static bool? _forced;

    /// <summary>Apps use dark mode (Settings → Personalization → Colors → app mode).</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>The taskbar is dark (decides the tray glyph's color).</summary>
    public static bool TaskbarIsDark => ReadDword("SystemUsesLightTheme") != 1;

    public static event Action? Changed;

    public static void Initialize()
    {
        Apply(ReadDword("AppsUseLightTheme") != 1);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ReadDword("AppsUseLightTheme") != 1));
        };
    }

    /// <summary>Pins the theme (screenshots), ignoring Windows' setting.</summary>
    public static void Force(bool dark)
    {
        _forced = dark;
        Apply(dark);
    }

    static void Apply(bool dark)
    {
        if (_forced is bool f) dark = f;
        var app = Application.Current;
        if (app is null) return;
        var dicts = app.Resources.MergedDictionaries;
        var wanted = dark ? DarkUri : LightUri;
        var current = dicts.FirstOrDefault(d => d.Source == DarkUri || d.Source == LightUri);
        if (current?.Source == wanted && IsDark == dark) return;
        var next = new ResourceDictionary { Source = wanted };
        if (current is null) dicts.Insert(0, next);
        else dicts[dicts.IndexOf(current)] = next;
        IsDark = dark;
        Changed?.Invoke();
    }

    static int ReadDword(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v ? v : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
