using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Seerr;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// The e-mail address of the signed-in user, and its management by administrators.
/// </summary>
[ApiController]
[Route("MailConcierge")]
public sealed class AccountController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private const int MaxBodyBytes = 4096;
    private const int MaxImportBodyBytes = 262144;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly EmailService _email;
    private readonly SeerrSync _seerr;
    private readonly IUserManager _users;
    private readonly Func<Configuration.PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccountController"/> class.
    /// </summary>
    /// <param name="email">The e-mail service.</param>
    /// <param name="seerr">The Seerr synchronisation.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="config">Accessor of the current configuration.</param>
    public AccountController(EmailService email, SeerrSync seerr, IUserManager users, Func<Configuration.PluginConfiguration> config)
    {
        _email = email;
        _seerr = seerr;
        _users = users;
        _config = config;
    }

    /// <summary>
    /// Gets the e-mail settings of the signed-in user.
    /// </summary>
    /// <returns>The address and the pending address.</returns>
    [HttpGet("Me/Email")]
    [Authorize]
    public async Task<ActionResult> GetMine()
    {
        if (CurrentUserId() is not Guid id)
        {
            return Unauthorized();
        }

        var record = await _email.GetAsync(id).ConfigureAwait(false);
        return Json(StatusCodes.Status200OK, new { email = record?.Email, pendingEmail = record?.PendingEmail });
    }

    /// <summary>
    /// Starts an e-mail change for the signed-in user.
    /// </summary>
    /// <param name="request">The new address and the current password.</param>
    /// <returns>204 when the confirmation was sent, else 400, 423, 429 or 503 with a <c>code</c>.</returns>
    [HttpPost("Me/Email")]
    [Authorize]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> ChangeMine([FromBody] MeEmailRequest? request)
    {
        if (CurrentUserId() is not Guid id)
        {
            return Unauthorized();
        }

        if (request is null)
        {
            return Code(StatusCodes.Status400BadRequest, "invalidEmail");
        }

        var ip = HttpContext.Connection.RemoteIpAddress;
        var remote = ip is null ? string.Empty : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        var result = await _email.RequestChangeAsync(id, request.Email ?? string.Empty, request.CurrentPassword ?? string.Empty, remote).ConfigureAwait(false);
        return result switch
        {
            ChangeResult.Ok => Done(),

            // Not 401: the web client signs the user out on a 401.
            ChangeResult.WrongPassword => Code(StatusCodes.Status403Forbidden, "wrongPassword"),
            ChangeResult.RateLimited => Code(StatusCodes.Status429TooManyRequests, "rateLimited"),

            // 423, not 429: the client shows a different message (the password is not checked for the rest of the hour).
            ChangeResult.PasswordLocked => Code(StatusCodes.Status423Locked, "passwordLocked"),
            ChangeResult.Unavailable => Code(StatusCodes.Status503ServiceUnavailable, "unavailable"),

            // Disabled or blocked by the proxy guard: the client says so instead of the "e-mail not set up" message.
            ChangeResult.Closed => Code(StatusCodes.Status503ServiceUnavailable, "closed"),
            _ => Code(StatusCodes.Status400BadRequest, "invalidEmail"),
        };
    }

    /// <summary>
    /// Lists the e-mail state of the users that have one, for the "no e-mail" indicator.
    /// </summary>
    /// <returns>The user id, the address and whether it is confirmed.</returns>
    [HttpGet("Users/Emails")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> ListEmails()
    {
        var all = await _email.ListAsync().ConfigureAwait(false);
        return Json(
            StatusCodes.Status200OK,
            all.Select(e => new { userId = e.UserId, email = e.Email, confirmed = !string.IsNullOrWhiteSpace(e.Email) && e.ConfirmedAt is not null }).ToList());
    }

    /// <summary>
    /// Sets the e-mail address of a user, confirmed immediately.
    /// </summary>
    /// <param name="id">The user id.</param>
    /// <param name="request">The address (blank to remove).</param>
    /// <returns>204, 400 or 404.</returns>
    [HttpPut("Users/{id}/Email")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> SetForUser([FromRoute] Guid id, [FromBody] AdminEmailRequest? request)
    {
        if (request is null || _users.GetUserById(id) is null)
        {
            return request is null ? Code(StatusCodes.Status400BadRequest, "invalidEmail") : NotFound();
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && !EmailService.TryNormalize(request.Email, out _))
        {
            return Code(StatusCodes.Status400BadRequest, "invalidEmail");
        }

        await _email.SetByAdminAsync(id, request.Email).ConfigureAwait(false);
        return Done();
    }

    /// <summary>
    /// Pushes now the addresses that could not be pushed to Seerr earlier.
    /// </summary>
    /// <returns>The number of users pushed, or 503 with <c>seerr_disabled</c> / <c>seerr_unreachable</c>.</returns>
    [HttpPost("Seerr/SyncNow")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> SeerrSyncNow()
    {
        try
        {
            return Json(StatusCodes.Status200OK, new { synced = await _seerr.SyncNowAsync().ConfigureAwait(false) });
        }
        catch (SeerrUnavailableException ex)
        {
            return Code(StatusCodes.Status503ServiceUnavailable, ex.Code);
        }
    }

    /// <summary>
    /// Lists the Seerr addresses that can be imported, for review.
    /// </summary>
    /// <returns>The user id, the user name, the Seerr address and the current Mail Concierge address, or 503 with <c>seerr_disabled</c> / <c>seerr_unreachable</c>.</returns>
    [HttpGet("Seerr/ImportPreview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> SeerrImportPreview()
    {
        try
        {
            var candidates = await _seerr.PreviewImportAsync().ConfigureAwait(false);
            return Json(
                StatusCodes.Status200OK,
                candidates.Select(c => new { userId = c.UserId, username = c.Username, email = c.Email, currentEmail = c.CurrentEmail }).ToList());
        }
        catch (SeerrUnavailableException ex)
        {
            return Code(StatusCodes.Status503ServiceUnavailable, ex.Code);
        }
    }

    /// <summary>
    /// Imports the selected Seerr addresses as confirmed addresses.
    /// </summary>
    /// <param name="request">The pairs of user id and address seen in the preview.</param>
    /// <returns>The number of addresses imported and the number that changed in Seerr since the preview, or 503 with <c>seerr_disabled</c> / <c>seerr_unreachable</c>.</returns>
    [HttpPost("Seerr/Import")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [RequestSizeLimit(MaxImportBodyBytes)]
    public async Task<ActionResult> SeerrImport([FromBody] SeerrImportRequest? request)
    {
        if (request?.Items is null)
        {
            return Code(StatusCodes.Status400BadRequest, "invalidRequest");
        }

        try
        {
            var result = await _seerr.ApplyImportAsync(request.Items).ConfigureAwait(false);
            return Json(StatusCodes.Status200OK, new { imported = result.Imported, changed = result.Changed });
        }
        catch (SeerrUnavailableException ex)
        {
            return Code(StatusCodes.Status503ServiceUnavailable, ex.Code);
        }
    }

    private Guid? CurrentUserId()
        => Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? id : null;

    private ActionResult Done()
    {
        Response.Headers.CacheControl = "no-store";
        return NoContent();
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
