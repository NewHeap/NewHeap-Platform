using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// Starts loopback test servers that the assistant reaches under the host name <c>localhost</c>.
/// </summary>
/// <remarks>
/// <c>localhost</c> resolves to <c>::1</c> before <c>127.0.0.1</c> on Windows, and a refused loopback
/// connection fails there only after about two seconds. A server that listened on <c>127.0.0.1</c>
/// alone would cost that on every new connection, so these servers listen on one port of both
/// loopback addresses, as Kestrel does for a <c>localhost</c> address with a fixed port.
/// </remarks>
internal static class LocalhostTestServer
{
    private const int MaxAttempts = 5;

    /// <summary>
    /// Builds a server for a free port and starts it; the server binds that port as
    /// <c>localhost</c>, for example with <c>UseUrls($"http://localhost:{port}")</c>. A port that
    /// another socket took in the meantime is replaced by a new one.
    /// </summary>
    public static async Task<WebApplication> StartAsync(Func<int, WebApplication> build)
    {
        for (var attempt = 1; ; attempt++)
        {
            var app = build(FreePort());
            try
            {
                await app.StartAsync();
                return app;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                // Kestrel reports a port in use as an IOException.
                await app.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Starts a plain http server without logging that answers with what <paramref name="map"/> adds.
    /// </summary>
    public static Task<WebApplication> StartPlainAsync(Action<WebApplication> map)
    {
        return StartAsync(port =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://localhost:{port}");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            map(app);
            return app;
        });
    }

    /// <summary>
    /// The origin of a started server, for example <c>http://localhost:5123</c>.
    /// </summary>
    public static string Origin(WebApplication app)
    {
        return app.Urls.First().TrimEnd('/');
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
