using System;
using System.Net;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Seerr;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

    [Fact]
    public async Task Response_OverTheCap_IsSeerrUnavailable()
    {
        var client = ClientAnswering(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(JsonOfLength(SeerrClient.MaxResponseBytes + 1)) });

        var ex = await Assert.ThrowsAsync<SeerrUnavailableException>(() => client.FindUserIdAsync(Guid.NewGuid()));

        Assert.Equal(SeerrUnavailableException.Unreachable, ex.Code);
    }

    [Fact]
    public async Task Response_AnnouncedOverTheCap_IsRefusedBeforeReading()
    {
        var content = new UnsizedContent(JsonOfLength(100), failOnRead: true);
        content.Headers.ContentLength = SeerrClient.MaxResponseBytes + 1;
        var client = ClientAnswering(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var ex = await Assert.ThrowsAsync<SeerrUnavailableException>(() => client.FindUserIdAsync(Guid.NewGuid()));

        Assert.Equal(SeerrUnavailableException.Unreachable, ex.Code);
    }

    [Fact]
    public async Task Response_AtTheCap_IsRead()
    {
        var client = ClientAnswering(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(JsonOfLength(SeerrClient.MaxResponseBytes)) });

        Assert.Equal(7, await client.FindUserIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Request_HeadersAndBodyShareOneTimeLimit()
    {
        var limit = TimeSpan.FromMilliseconds(400);
        var config = new PluginConfiguration { SeerrEnabled = true, SeerrUrl = "https://seerr.example/", SeerrApiKey = "sekret-key" };
        var handler = new SlowHandler(TimeSpan.FromMilliseconds(240));
        var client = new SeerrClient(new StubFactory(handler), () => config, NullLogger<SeerrClient>.Instance, limit, TimeProvider.System);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Record.ExceptionAsync(() => client.FindUserIdAsync(Guid.NewGuid()));

        watch.Stop();
        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.DoesNotContain("sekret-key", cancelled.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(560), "The call took " + watch.ElapsedMilliseconds + " ms, more than one time limit.");
    }

    [Fact]
    public async Task ListUsers_MoreThanTenThousandUsers_IsSeerrUnavailable()
    {
        var pages = 0;
        var client = ClientAnswering(() => UsersPage(pages++ * 100, 100, 50_000));

        var ex = await Assert.ThrowsAsync<SeerrUnavailableException>(() => client.ListUsersAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SeerrUnavailableException.Unreachable, ex.Code);
        Assert.True(pages <= 101, "The listing went on after the bound: " + pages + " pages.");
    }

    [Fact]
    public async Task ListUsers_ExactlyTenThousandUsers_IsListed()
    {
        var pages = 0;
        var client = ClientAnswering(() => UsersPage(pages++ * 100, 100, 10_000));

        var users = await client.ListUsersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10_000, users.Count);
    }

    [Fact]
    public async Task ListUsers_PageLargerThanRequested_IsSeerrUnavailable()
    {
        var client = ClientAnswering(() => UsersPage(0, 101, 101));

        var ex = await Assert.ThrowsAsync<SeerrUnavailableException>(() => client.ListUsersAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SeerrUnavailableException.Unreachable, ex.Code);
    }

    [Fact]
    public async Task ListUsers_RunningPastSixtySeconds_IsCancelledOnTheInjectedClock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var pages = 0;
        var config = new PluginConfiguration { SeerrEnabled = true, SeerrUrl = "https://seerr.example/", SeerrApiKey = "sekret-key" };
        var handler = new StubHandler(() =>
        {
            time.Advance(TimeSpan.FromSeconds(25));
            return UsersPage(pages++ * 100, 100, 5_000);
        });
        var client = new SeerrClient(new StubFactory(handler), () => config, NullLogger<SeerrClient>.Instance, time);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListUsersAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task ListUsers_CallerTokenCancelled_StopsTheListing()
    {
        var pages = 0;
        using var source = new CancellationTokenSource();
        var client = ClientAnswering(() =>
        {
            if (pages++ == 1)
            {
                source.Cancel();
            }

            return UsersPage((pages - 1) * 100, 100, 5_000);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListUsersAsync(source.Token));

        Assert.Equal(2, pages);
    }

    private static HttpResponseMessage UsersPage(int first, int count, int total)
    {
        var items = new List<string>();
        for (var i = 0; i < count; i++)
        {
            items.Add("{\"id\":" + (first + i + 1) + ",\"email\":\"u" + (first + i) + "@example.org\"}");
        }

        var json = "{\"pageInfo\":{\"results\":" + total + ",\"pageSize\":100},\"results\":[" + string.Join(',', items) + "]}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static SeerrClient ClientAnswering(Func<HttpResponseMessage> answer)
    {
        var config = new PluginConfiguration { SeerrEnabled = true, SeerrUrl = "https://seerr.example/", SeerrApiKey = "sekret-key" };
        return new SeerrClient(new StubFactory(new StubHandler(answer)), () => config, NullLogger<SeerrClient>.Instance, TimeProvider.System);
    }

    // A JSON object {"id":7,"pad":"...."} of exactly the given length in bytes.
    private static byte[] JsonOfLength(int length)
    {
        const string Head = "{\"id\":7,\"pad\":\"";
        const string Tail = "\"}";
        return Encoding.UTF8.GetBytes(Head + new string('a', length - Head.Length - Tail.Length) + Tail);
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

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(answer());
    }

    // Content without a known length, as a chunked response would be.
    private sealed class UnsizedContent(byte[] bytes, bool failOnRead = false) : HttpContent
    {
        protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
            => failOnRead ? throw new InvalidOperationException("The body must not be read.") : stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    // Answers the headers after a delay, then a body that never completes.
    private sealed class SlowHandler(TimeSpan headersDelay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            await Task.Delay(headersDelay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() };
        }
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
            => Task.Delay(System.Threading.Timeout.Infinite);

        protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context, System.Threading.CancellationToken cancellationToken)
            => Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
