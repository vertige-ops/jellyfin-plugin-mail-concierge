using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

public sealed class IndexInjectionTests
{
    private const string Index = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Jellyfin</title></head><body>é</body></html>";

    private readonly PluginConfiguration _config = new();

    [Fact]
    public async Task Injects_ScriptOnce_IntoIndexHtml()
    {
        foreach (var path in new[] { "/web/", "/web/index.html" })
        {
            var (body, response) = await Run(string.Empty, path, Index, "text/html");

            var tag = ScriptTag(string.Empty);
            Assert.Equal(1, Count(body, tag));
            Assert.Contains(tag + "</head>", body, StringComparison.Ordinal);
            Assert.Contains("<body>é</body>", body, StringComparison.Ordinal);
            Assert.Null(response.ContentLength);
            Assert.False(response.Headers.ContainsKey("Content-Length"));
        }

        // Already present: not injected a second time.
        var twice = Index.Replace("</head>", "<script src=\"/MailConcierge/client.js\" defer></script></head>", StringComparison.Ordinal);
        var (again, _) = await Run(string.Empty, "/web/index.html", twice, "text/html");
        Assert.Equal(1, Count(again, "/MailConcierge/client.js"));
    }

    [Fact]
    public async Task DoesNotTouch_OtherPaths()
    {
        foreach (var (path, type) in new[] { ("/web/main.js", "text/javascript"), ("/Users", "application/json") })
        {
            var (body, response) = await Run(string.Empty, path, Index, type);

            Assert.Equal(Index, body);
            Assert.Equal(Encoding.UTF8.GetByteCount(Index), response.ContentLength);
        }

        // Disabled plugin: index.html is passed through untouched.
        _config.Enabled = false;
        var (disabled, _) = await Run(string.Empty, "/web/index.html", Index, "text/html");
        Assert.Equal(Index, disabled);
    }

    [Fact]
    public async Task DoesNothing_WhenHeadMissing()
    {
        const string NoHead = "<html><body>no head here</body></html>";

        var (body, response) = await Run(string.Empty, "/web/index.html", NoHead, "text/html");
        Assert.Equal(NoHead, body);
        Assert.Equal(Encoding.UTF8.GetByteCount(NoHead), response.ContentLength);

        // Not HTML: untouched even on the index path.
        var (json, _) = await Run(string.Empty, "/web/index.html", Index, "application/json");
        Assert.Equal(Index, json);
    }

    [Fact]
    public async Task RespectsBaseUrl()
    {
        var (body, _) = await Run("/jf", "/jf/web/index.html", Index, "text/html");
        Assert.Contains(ScriptTag("/jf") + "</head>", body, StringComparison.Ordinal);

        // Without the base URL prefix, the path is not the web client.
        var (other, _) = await Run("/jf", "/web/index.html", Index, "text/html");
        Assert.Equal(Index, other);
    }

    [Fact]
    public async Task OtherPaths_KeepAcceptEncoding_AndConditionalHeaders()
    {
        string? encoding = null;
        string? etag = null;
        await Run(string.Empty, "/web/main.js", Index, "text/javascript", HttpMethods.Get, SetRequestHeaders, ctx => Capture(ctx, out encoding, out etag));
        Assert.Equal("gzip, br", encoding);
        Assert.Equal("\"abc\"", etag);

        await Run(string.Empty, "/web/index.html", Index, "text/html", HttpMethods.Get, SetRequestHeaders, ctx => Capture(ctx, out encoding, out etag));
        Assert.Equal(string.Empty, encoding);
        Assert.Equal(string.Empty, etag);

        static void SetRequestHeaders(IHeaderDictionary h)
        {
            h.AcceptEncoding = "gzip, br";
            h.IfNoneMatch = "\"abc\"";
        }

        static void Capture(HttpContext ctx, out string? encoding, out string? etag)
        {
            encoding = ctx.Request.Headers.AcceptEncoding.ToString();
            etag = ctx.Request.Headers.IfNoneMatch.ToString();
        }
    }

    [Fact]
    public async Task IndexWithContentEncoding_PassesThrough_ByteIdentical()
    {
        var (body, response) = await Run(string.Empty, "/web/index.html", Index, "text/html", HttpMethods.Get, null, ctx => ctx.Response.Headers.ContentEncoding = "gzip");

        Assert.Equal(Index, body);
        Assert.Equal(Encoding.UTF8.GetByteCount(Index), response.ContentLength);
    }

    [Fact]
    public async Task PostToIndex_IsNotBuffered_NorModified()
    {
        var streamSeen = (Stream?)null;
        string? encoding = null;
        var (body, response) = await Run(
            string.Empty,
            "/web/index.html",
            Index,
            "text/html",
            HttpMethods.Post,
            h => h.AcceptEncoding = "gzip",
            ctx =>
            {
                streamSeen = ctx.Response.Body;
                encoding = ctx.Request.Headers.AcceptEncoding.ToString();
            });

        Assert.Equal(Index, body);
        Assert.Equal(Encoding.UTF8.GetByteCount(Index), response.ContentLength);
        Assert.Same(response.Body, streamSeen);
        Assert.Equal("gzip", encoding);
    }

    [Fact]
    public void ClientJs_IsServed_WithStrings()
    {
        var controller = new ClientScriptController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = Assert.IsType<ContentResult>(controller.Get());

        Assert.StartsWith("text/javascript", result.ContentType, StringComparison.Ordinal);
        Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
        Assert.NotNull(result.Content);
        Assert.DoesNotContain("/*MAILCONCIERGE_STRINGS*/", result.Content, StringComparison.Ordinal);

        var start = result.Content!.IndexOf("const STRINGS = ", StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += "const STRINGS = ".Length;
        var end = result.Content.IndexOf(";\n", start, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(result.Content[start..end]);
        var en = json.RootElement.GetProperty("en");
        var fr = json.RootElement.GetProperty("fr");
        Assert.Equal("If this account has an e-mail address, a code has just been sent to it.", en.GetProperty("forgot.neutral").GetString());
        Assert.Equal("Si ce compte a une adresse e-mail, un code vient d'y être envoyé.", fr.GetProperty("forgot.neutral").GetString());
        Assert.True(en.TryGetProperty("profile.email", out _));
        Assert.True(fr.TryGetProperty("profile.email", out _));
        Assert.Equal(
            en.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
            fr.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task InjectedScriptUrl_CarriesPluginVersion_ToBustStaleCaches()
    {
        var (body, _) = await Run(string.Empty, "/web/index.html", Index, "text/html");

        var version = typeof(Plugin).Assembly.GetName().Version!.ToString();
        Assert.Contains("src=\"/MailConcierge/client.js?v=" + version + "\"", body, StringComparison.Ordinal);
    }

    private static string ScriptTag(string baseUrl)
        => "<script src=\"" + baseUrl + "/MailConcierge/client.js?v=" + typeof(Plugin).Assembly.GetName().Version + "\" defer></script>";

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private Task<(string Body, HttpResponse Response)> Run(string baseUrl, string path, string content, string type)
        => Run(baseUrl, path, content, type, HttpMethods.Get, null, null);

    private async Task<(string Body, HttpResponse Response)> Run(
        string baseUrl,
        string path,
        string content,
        string type,
        string method,
        Action<IHeaderDictionary>? requestHeaders,
        Action<HttpContext>? inner)
    {
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.GetConfiguration("network")).Returns(new NetworkConfiguration { BaseUrl = baseUrl });
        var filter = new IndexInjectionStartupFilter(serverConfig.Object, () => _config);

        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        filter.Configure(builder => builder.Run(async ctx =>
        {
            inner?.Invoke(ctx);
            var bytes = Encoding.UTF8.GetBytes(content);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = type + "; charset=utf-8";
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes);
        }))(app);
        var pipeline = app.Build();

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        requestHeaders?.Invoke(context.Request.Headers);
        using var output = new MemoryStream();
        context.Response.Body = output;

        await pipeline(context);

        return (Encoding.UTF8.GetString(output.ToArray()), context.Response);
    }
}
