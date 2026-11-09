using System.Windows.Threading;
using Gyrodial.App.UI;
using Gyrodial.Core;

namespace Gyrodial.App;

/// <summary>What the app lends every integration: settings, the log, notifications, the UI thread.</summary>
public sealed class IntegrationHost(SettingsStore settings, TrayIcon tray, Dispatcher ui) : IIntegrationHost
{
    public SettingsStore Settings => settings;

    public void Log(string source, string message) => AppLog.Instance.Info(source, message);

    public void ShowNotification(string title, string message) =>
        ui.BeginInvoke(() => tray.ShowNotification(title, message));

    public void RunOnUi(Action action) => ui.BeginInvoke(() =>
    {
        try { action(); }
        catch (Exception e) { AppLog.Instance.Error("ui", "integration UI action failed", e); }
    });
}

/// <summary>One copy of Gyrodial per user: a second launch just opens the first one's flyout.</summary>
public static class SingleInstance
{
    const string MutexName = @"Local\Gyrodial.App.SingleInstance";
    const string ShowEventName = @"Local\Gyrodial.App.Show";

    /// <summary>The mutex if this is the first instance (keep it alive), else null.</summary>
    public static Mutex? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (created) return mutex;
        mutex.Dispose();
        return null;
    }

    public static void SignalFirstInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
    }

    /// <summary>Calls <paramref name="onShow"/> (on a background thread) whenever another launch asks.</summary>
    public static void Listen(Action onShow)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (ev.WaitOne()) onShow();
        }) { IsBackground = true, Name = "Gyrodial single instance" }.Start();
    }
}
