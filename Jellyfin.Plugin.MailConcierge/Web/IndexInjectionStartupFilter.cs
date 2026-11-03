using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Adds the Mail Concierge client script to the index page of the web client.
/// </summary>
/// <remarks>
/// Jellyfin builds its host with <c>Host.CreateDefaultBuilder().ConfigureServices(appHost.Init)</c>, and
/// <c>appHost.Init</c> calls the plugin service registrators, so a registered <see cref="IStartupFilter"/> is
/// applied by ASP.NET Core around <c>Startup.Configure</c>. The middleware therefore runs before
/// <c>app.Map(BaseUrl)</c>: the request path still contains the base URL, and the response is not compressed yet
/// when the client did not ask for compression.
/// </remarks>
public sealed class IndexInjectionStartupFilter : IStartupFilter
{
    private const string ScriptPath = "/MailConcierge/client.js";
    private static readonly byte[] HeadEnd = Encoding.ASCII.GetBytes("</head>");

    // The version in the query string makes a new plugin version fetch the script again, whatever the browser cached.
    private static readonly string ScriptUrlSuffix = "?v=" + typeof(IndexInjectionStartupFilter).Assembly.GetName().Version;
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes(ScriptPath);

    private readonly IServerConfigurationManager _serverConfig;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="serverConfig">The server configuration manager (base URL).</param>
    /// <param name="config">Accessor of the current configuration.</param>
    public IndexInjectionStartupFilter(IServerConfigurationManager serverConfig, Func<PluginConfiguration> config)
    {
        _serverConfig = serverConfig;
        _config = config;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var baseUrl = BaseUrl();
        if (baseUrl is null || !HttpMethods.IsGet(context.Request.Method) || !IsIndexPath(context.Request.Path, baseUrl) || !Enabled())
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // The page is small: ask for it uncompressed and always in full, so that the injected copy is never stale.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        var bytes = buffer.ToArray();
        var injected = CanRewrite(context.Response) ? Inject(bytes, baseUrl) : null;
        if (injected is not null)
        {
            context.Response.Headers.ContentLength = null;
            context.Response.Headers.ETag = default;
            bytes = injected;
        }

        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsIndexPath(PathString path, string baseUrl)
    {
        var value = path.Value ?? string.Empty;
        if (!value.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[baseUrl.Length..];
        return rest.Equals("/web/", StringComparison.OrdinalIgnoreCase) || rest.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanRewrite(HttpResponse response)
        => response.StatusCode == StatusCodes.Status200OK
            && response.Headers.ContentEncoding.Count == 0
            && response.ContentType is { } type
            && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

    private static byte[]? Inject(byte[] page, string baseUrl)
    {
        // Byte-level search and insert: every other byte is kept, whatever the charset (ASCII-compatible).
        var head = page.AsSpan().IndexOf(HeadEnd);
        if (head < 0 || page.AsSpan().IndexOf(Marker) >= 0)
        {
            return null;
        }

        var tag = Encoding.UTF8.GetBytes("<script src=\"" + WebUtility.HtmlEncode(baseUrl + ScriptPath + ScriptUrlSuffix) + "\" defer></script>");
        var result = new byte[page.Length + tag.Length];
        page.AsSpan(0, head).CopyTo(result);
        tag.CopyTo(result.AsSpan(head));
        page.AsSpan(head).CopyTo(result.AsSpan(head + tag.Length));
        return result;
    }

    private string? BaseUrl()
    {
        try
        {
            return _serverConfig.GetNetworkConfiguration().BaseUrl?.TrimEnd('/') ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private bool Enabled()
    {
        try
        {
            return _config().Enabled;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
