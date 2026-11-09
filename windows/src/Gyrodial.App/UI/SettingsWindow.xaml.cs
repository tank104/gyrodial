using System.Windows;
using System.Windows.Controls;

namespace Gyrodial.App.UI;

public enum SettingsPage { General, Dial, Mouse, Claude, Integrations, Firmware, Log, About }

/// <summary>Settings: a left navigation list of pages, on Mica where Windows has it.</summary>
public partial class SettingsWindow : Window
{
    readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.Changed += ApplyChrome;
    }

    void ApplyChrome()
    {
        bool mica = WindowEffects.TryApplyBackdrop(this, Backdrop.Mica, ThemeManager.IsDark);
        // On Mica the window draws only a faint tint over it; otherwise the solid window color
        if (mica)
        {
            Background = System.Windows.Media.Brushes.Transparent;
            Root.SetResourceReference(Panel.BackgroundProperty, "Brush.MicaTint");
        }
        else
        {
            SetResourceReference(BackgroundProperty, "Brush.Window");
            Root.Background = null;
        }
    }

    public void ShowPage(SettingsPage page)
    {
        Nav.SelectedIndex = (int)page;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Nav) return;
        if (Nav.SelectedItem == LogTab) _vm.Log.Attach();
    }

    void OnSaveApiKey(object sender, RoutedEventArgs e)
    {
        if (ApiKeyBox.Password.Length == 0) return;
        _vm.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    void OnClearApiKey(object sender, RoutedEventArgs e)
    {
        _vm.SaveApiKey("");
        ApiKeyBox.Clear();
    }

    void OnFirmwareLogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) box.ScrollToEnd();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Settings is reopened often: keep the window, just hide it
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.Changed -= ApplyChrome;
        base.OnClosed(e);
    }
}
