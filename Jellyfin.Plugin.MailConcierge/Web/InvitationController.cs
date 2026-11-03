using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Invitations;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Invitations, for administrators.
/// </summary>
[ApiController]
[Route("MailConcierge")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class InvitationController : ControllerBase
{
    private const int MaxBodyBytes = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly InvitationService _invitations;
    private readonly ILibraryManager _libraries;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="InvitationController"/> class.
    /// </summary>
    /// <param name="invitations">The invitation service.</param>
    /// <param name="libraries">The library manager.</param>
    /// <param name="config">Accessor of the current configuration.</param>
    public InvitationController(InvitationService invitations, ILibraryManager libraries, Func<PluginConfiguration> config)
    {
        _invitations = invitations;
        _libraries = libraries;
        _config = config;
    }

    /// <summary>
    /// Lists the invitations. Links and token hashes are never returned.
    /// </summary>
    /// <returns>The invitations, newest first.</returns>
    [HttpGet("Invitations")]
    public async Task<ActionResult> List()
    {
        var all = await _invitations.ListAsync().ConfigureAwait(false);
        return Json(
            StatusCodes.Status200OK,
            all.Select(i => new
            {
                id = i.Id,
                proposedUsername = i.ProposedUsername,
                email = i.Email,
                libraryIds = i.LibraryIds,
                language = i.Language,
                expiresAt = DateTime.SpecifyKind(i.ExpiresAt, DateTimeKind.Utc),
                createdAt = DateTime.SpecifyKind(i.CreatedAt, DateTimeKind.Utc),
                state = i.State.ToString(),
            }).ToList());
    }

    /// <summary>
    /// Creates an invitation, and sends it when an address is given.
    /// </summary>
    /// <param name="request">The invitation.</param>
    /// <returns>The id, the link (shown only now), whether an e-mail was queued, and why not when it was not (<c>emailStatus</c>).</returns>
    [HttpPost("Invitations")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> Create([FromBody] CreateInvitationRequest? request)
    {
        if (request is null)
        {
            return Code(StatusCodes.Status400BadRequest, "invalid");
        }

        var libraryIds = request.LibraryIds ?? Array.Empty<string>();
        var known = KnownLibraries();
        if (libraryIds.Any(l => !Guid.TryParse(l, out var id) || !known.ContainsKey(id)))
        {
            return Code(StatusCodes.Status400BadRequest, "invalid_library");
        }

        try
        {
            var (inv, link, email) = await _invitations.CreateAsync(request.ProposedUsername ?? string.Empty, request.Email, libraryIds, request.Days, request.Language ?? string.Empty).ConfigureAwait(false);
            var emailStatus = email switch
            {
                EnqueueResult.Queued => "queued",
                EnqueueResult.LimitReached => "limit_reached",
                EnqueueResult.NotConfigured => "not_configured",
                _ => "none",
            };
            return Json(StatusCodes.Status200OK, new { id = inv.Id, link, emailed = email == EnqueueResult.Queued, emailStatus });
        }
        catch (InvalidOperationException)
        {
            return Code(StatusCodes.Status400BadRequest, "no_public_url");
        }
        catch (ArgumentException ex)
        {
            return Code(StatusCodes.Status400BadRequest, ex.ParamName == "email" ? "invalid_email" : ex.ParamName == "proposedUsername" ? "invalid_username" : "invalid");
        }
    }

    /// <summary>
    /// Revokes an invitation.
    /// </summary>
    /// <param name="id">The invitation id.</param>
    /// <returns>200 with an empty object.</returns>
    [HttpDelete("Invitations/{id}")]
    public async Task<ActionResult> Revoke([FromRoute] Guid id)
    {
        await _invitations.RevokeAsync(id).ConfigureAwait(false);

        // A body, because the admin page reads every response as JSON.
        return Json(StatusCodes.Status200OK, new { });
    }

    /// <summary>
    /// Gives an invitation a new link and restarts its validity.
    /// </summary>
    /// <param name="id">The invitation id.</param>
    /// <returns>The new link (shown only now), or 404.</returns>
    [HttpPost("Invitations/{id}/Regenerate")]
    public async Task<ActionResult> Regenerate([FromRoute] Guid id)
    {
        try
        {
            return Json(StatusCodes.Status200OK, new { link = await _invitations.RegenerateAsync(id).ConfigureAwait(false) });
        }
        catch (KeyNotFoundException)
        {
            return Code(StatusCodes.Status404NotFound, "not_found");
        }
        catch (InvalidOperationException)
        {
            return Code(StatusCodes.Status400BadRequest, "no_public_url");
        }
    }

    /// <summary>
    /// Lists the libraries an invitation can grant.
    /// </summary>
    /// <returns>The id and name of each library.</returns>
    [HttpGet("Libraries")]
    public ActionResult Libraries()
        => Json(StatusCodes.Status200OK, KnownLibraries().Select(l => new { id = l.Key.ToString("N"), name = l.Value }).ToList());

    private Dictionary<Guid, string> KnownLibraries()
    {
        var result = new Dictionary<Guid, string>();
        foreach (var folder in _libraries.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                result[id] = folder.Name;
            }
        }

        return result;
    }

    private ContentResult Code(int status, string code) => Json(status, new Dictionary<string, string> { ["code"] = code });

    private ContentResult Json(int status, object body)
    {
        Response.Headers.CacheControl = "no-store";
        return new ContentResult
        {
            StatusCode = status,
            ContentType = MediaTypeNames.Application.Json,
            Content = JsonSerializer.Serialize(body, JsonOptions),
        };
    }
}
