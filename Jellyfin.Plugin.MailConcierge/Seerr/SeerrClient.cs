using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// Minimal client of the Seerr REST API (<c>X-Api-Key</c> authentication). The base URL and key are read from the
/// configuration at each call. The key and the URL are never logged.
/// </summary>
public sealed class SeerrClient
{
    /// <summary>The name of the HTTP client requested from the factory.</summary>
    public const string HttpClientName = "MailConcierge.Seerr";

    private const int PageSize = 100;
    private const int MaxPages = 1000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _factory;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<SeerrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrClient"/> class.
    /// </summary>
    /// <param name="factory">The HTTP client factory.</param>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="logger">The logger.</param>
    public SeerrClient(IHttpClientFactory factory, Func<PluginConfiguration> config, ILogger<SeerrClient> logger)
    {
        _factory = factory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Creates the message handler of the Seerr HTTP client. Redirections are not followed: Seerr answers on the URL that
    /// was configured, and following a redirect would send the API key to another host.
    /// </summary>
    /// <returns>The handler.</returns>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    /// <summary>
    /// Finds the Seerr user linked to a Jellyfin user.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user id.</param>
    /// <returns>The Seerr user id, or null when Seerr has no such user.</returns>
    /// <exception cref="HttpRequestException">Seerr answered with an unexpected status.</exception>
    public async Task<int?> FindUserIdAsync(Guid jellyfinUserId)
    {
        // Seerr normalizes the id (dashes removed, lower case); the "N" format is what it stores.
        using var response = await SendAsync(HttpMethod.Get, "api/v1/user/jellyfin/" + jellyfinUserId.ToString("N"), null).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await ReadJsonAsync(response).ConfigureAwait(false);
        return body["id"]?.GetValue<int>();
    }

    /// <summary>
    /// Sets the e-mail address of a Seerr user. The settings are read first and posted back unchanged except for the address,
    /// because Seerr overwrites the user name, locale, regions and watchlist flags (and sometimes the quotas) from the body.
    /// </summary>
    /// <param name="seerrUserId">The Seerr user id.</param>
    /// <param name="email">The new address.</param>
    /// <returns>True when Seerr holds the address afterwards; false when Seerr refused it (address used by another Seerr user).</returns>
    /// <exception cref="HttpRequestException">Seerr answered with an unexpected status.</exception>
    public async Task<bool> SetEmailAsync(int seerrUserId, string email)
    {
        var path = "api/v1/user/" + seerrUserId.ToString(CultureInfo.InvariantCulture) + "/settings/main";
        JsonObject settings;
        using (var get = await SendAsync(HttpMethod.Get, path, null).ConfigureAwait(false))
        {
            get.EnsureSuccessStatusCode();
            settings = await ReadJsonAsync(get).ConfigureAwait(false);
        }

        if (string.Equals((string?)settings["email"], email, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        settings["email"] = email;
        using var post = await SendAsync(HttpMethod.Post, path, settings.ToJsonString()).ConfigureAwait(false);
        if (post.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("Seerr refused the e-mail of Seerr user {SeerrUserId} (address already used by another Seerr user?)", seerrUserId);
            return false;
        }

        post.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Lists all Seerr users, page by page.
    /// </summary>
    /// <returns>The Seerr id, the linked Jellyfin user (null for local users) and the address of every user.</returns>
    /// <exception cref="HttpRequestException">Seerr answered with an unexpected status.</exception>
    public async Task<IReadOnlyList<(int Id, Guid? JellyfinUserId, string Email)>> ListUsersAsync()
    {
        var users = new List<(int, Guid?, string)>();
        for (var page = 0; page < MaxPages; page++)
        {
            var skip = page * PageSize;
            using var response = await SendAsync(HttpMethod.Get, "api/v1/user?take=" + PageSize.ToString(CultureInfo.InvariantCulture) + "&skip=" + skip.ToString(CultureInfo.InvariantCulture), null).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await ReadJsonAsync(response).ConfigureAwait(false);
            var results = body["results"]?.AsArray() ?? [];
            foreach (var item in results)
            {
                if (item?["id"]?.GetValue<int>() is not int id)
                {
                    continue;
                }

                Guid? jellyfinId = Guid.TryParse((string?)item["jellyfinUserId"], out var parsed) ? parsed : null;
                users.Add((id, jellyfinId, (string?)item["email"] ?? string.Empty));
            }

            var total = body["pageInfo"]?["results"]?.GetValue<int>();
            if (results.Count < PageSize || (total is int t && skip + results.Count >= t))
            {
                break;
            }
        }

        return users;
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonObject ?? throw new JsonException("Unexpected Seerr response.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, string? json)
    {
        var cfg = _config();
        if (!Uri.TryCreate(cfg.SeerrUrl?.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(cfg.SeerrApiKey))
        {
            throw new InvalidOperationException("Seerr is not configured.");
        }

        if (!baseUri.AbsolutePath.EndsWith('/'))
        {
            baseUri = new UriBuilder(baseUri) { Path = baseUri.AbsolutePath + "/" }.Uri;
        }

        using var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        request.Headers.Add("X-Api-Key", cfg.SeerrApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        using var client = _factory.CreateClient(HttpClientName);
        client.Timeout = Timeout;
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
