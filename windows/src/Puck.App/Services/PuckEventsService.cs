using Puck.Core;

namespace Puck.App.Services;

/// <summary>The small things the puck says that need nothing but a notification or a log line.</summary>
public sealed class PuckEventsService : IDisposable
{
    readonly List<IDisposable> _subs = [];

    public PuckEventsService(IPuckHub hub, IIntegrationHost host)
    {
        _subs.Add(hub.On(Protocol.T.Focus, m =>
        {
            if (m.GetString("s") != "done") return;
            int min = m.GetInt("min", 0);
            host.ShowNotification("Focus session done", min > 0 ? $"{min} min of focus. Take a break." : "Take a break.");
        }));
        _subs.Add(hub.On(Protocol.T.Log, m => host.Log("puck", m.GetString("m", ""))));
    }

    public void Dispose()
    {
        foreach (var s in _subs) s.Dispose();
    }
}
