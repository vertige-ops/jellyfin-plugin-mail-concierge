using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Seerr;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Seerr;

public sealed class SeerrHttpClientTests
{
    [Fact]
    public void Handler_DoesNotFollowRedirects()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(SeerrClient.CreateHandler());

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task NamedClient_FromTheRegisteredServices_ReturnsTheRedirectInsteadOfFollowingIt()
    {
        var target = new TcpListenerServer("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        var first = new TcpListenerServer("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + target.Port + "/leak\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            new ServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SeerrClient.HttpClientName);

            using var response = await client.GetAsync(new Uri("http://127.0.0.1:" + first.Port + "/api"), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(0, target.Connections);
        }
        finally
        {
            first.Dispose();
            target.Dispose();
        }
    }

    private sealed class TcpListenerServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _response;
        private int _connections;

        public TcpListenerServer(string response)
        {
            _response = response;
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => _connections;

        public void Dispose() => _listener.Stop();

        private async Task AcceptLoop()
        {
            try
            {
                while (true)
                {
                    using var socket = await _listener.AcceptSocketAsync();
                    System.Threading.Interlocked.Increment(ref _connections);
                    var buffer = new byte[4096];
                    _ = await socket.ReceiveAsync(buffer);
                    _ = await socket.SendAsync(System.Text.Encoding.ASCII.GetBytes(_response));
                    socket.Shutdown(System.Net.Sockets.SocketShutdown.Both);
                }
            }
            catch (Exception)
            {
                // The listener was stopped.
            }
        }
    }
}
