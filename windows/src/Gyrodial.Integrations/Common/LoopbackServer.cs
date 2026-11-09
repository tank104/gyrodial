using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Gyrodial.Integrations.Common;

/// <summary>
/// A small Kestrel server bound to 127.0.0.1 only. Kestrel rather than HttpListener because
/// HttpListener needs an admin URL reservation to answer on "127.0.0.1", which browsers and the hook
/// use (the Host header has to match the prefix).
/// </summary>
public sealed class LoopbackServer : IAsyncDisposable
{
    readonly WebApplication _app;

    public const long MaxRequestBodyBytes = 1024 * 1024;

    public int Port { get; }

    LoopbackServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>Starts listening; <paramref name="map"/> adds the routes. Throws if the port is taken.</summary>
    public static async Task<LoopbackServer> StartAsync(int port, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Gyrodial",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = MaxRequestBodyBytes;  // a hook payload or a page message is far smaller
        });
        var app = builder.Build();
        map(app);
        await app.StartAsync().ConfigureAwait(false);
        return new LoopbackServer(app, port);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
