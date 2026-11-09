using Gyrodial.Core;

namespace Gyrodial.App.Services;

/// <summary>The small things the dial says that need nothing but a notification or a log line.</summary>
public sealed class DialEventsService : IDisposable
{
    readonly List<IDisposable> _subs = [];

    public DialEventsService(IDialHub hub, IIntegrationHost host)
    {
        _subs.Add(hub.On(Protocol.T.Focus, m =>
        {
            if (m.GetString("s") != "done") return;
            int min = m.GetInt("min", 0);
            host.ShowNotification("Focus session done", min > 0 ? $"{min} min of focus. Take a break." : "Take a break.");
        }));
        _subs.Add(hub.On(Protocol.T.Log, m => host.Log("dial", m.GetString("m", ""))));
    }

    public void Dispose()
    {
        foreach (var s in _subs) s.Dispose();
    }
}
