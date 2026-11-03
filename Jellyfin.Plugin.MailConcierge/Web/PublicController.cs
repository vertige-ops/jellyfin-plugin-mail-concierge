using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Invitations;
using Jellyfin.Plugin.MailConcierge.Localization;
using Jellyfin.Plugin.MailConcierge.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// The anonymous pages the user lands on from e-mails, and the recovery API used by those pages and by the sign-in script.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("MailConcierge")]
public sealed class PublicController : ControllerBase
{
    private const string UnknownClientKey = "unknown";
    private const int MaxBodyBytes = 4096;

    // A 1 MiB picture is about 1.4 MB in base64, plus the other fields.
    private const int MaxAcceptBodyBytes = 1_600_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RecoveryService _recovery;
    private readonly EmailService _email;
    private readonly InvitationService _invitations;
    private readonly PageRenderer _renderer;
    private readonly ProxyGuard _guard;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicController"/> class.
    /// </summary>
    /// <param name="recovery">The recovery service.</param>
    /// <param name="email">The e-mail service.</param>
    /// <param name="invitations">The invitation service.</param>
    /// <param name="renderer">The page renderer.</param>
    /// <param name="guard">The reverse-proxy guard.</param>
    public PublicController(RecoveryService recovery, EmailService email, InvitationService invitations, PageRenderer renderer, ProxyGuard guard)
    {
        _recovery = recovery;
        _email = email;
        _invitations = invitations;
        _renderer = renderer;
        _guard = guard;
    }

    /// <summary>
    /// Shows the new-password page. The token is not consumed.
    /// </summary>
    /// <param name="t">The token of the e-mailed link.</param>
    /// <param name="s">A reset session already obtained from a PIN.</param>
    /// <returns>The page.</returns>
    [HttpGet("Reset")]
    [Produces(MediaTypeNames.Text.Html)]
    public async Task<ActionResult> Reset([FromQuery] string? t, [FromQuery] string? s)
    {
        if (string.IsNullOrWhiteSpace(t) && string.IsNullOrWhiteSpace(s))
        {
            return Page("message");
        }

        // A link that is used, expired or unknown gets the same page as an invalid invitation.
        if (!string.IsNullOrWhiteSpace(t) && string.IsNullOrWhiteSpace(s) && !await _recovery.IsLinkValidAsync(t).ConfigureAwait(false))
        {
            return Page("message");
        }

        // The language the account's e-mails are sent in, the browser's only when the link tells nothing.
        return Page("reset", string.IsNullOrWhiteSpace(t) ? null : await _recovery.PeekLinkLanguageAsync(t).ConfigureAwait(false));
    }

    /// <summary>
    /// Shows the e-mail confirmation page. The token is not consumed.
    /// </summary>
    /// <param name="t">The token of the e-mailed link.</param>
    /// <returns>The page.</returns>
    [HttpGet("Confirm")]
    [Produces(MediaTypeNames.Text.Html)]
    public async Task<ActionResult> ConfirmPage([FromQuery] string? t)
        => string.IsNullOrWhiteSpace(t) ? Page("message") : Page("confirm", await _email.PeekLanguageAsync(t, TokenPurpose.ConfirmEmail).ConfigureAwait(false));

    /// <summary>
    /// Shows the e-mail change cancellation page. The token is not consumed.
    /// </summary>
    /// <param name="t">The token of the e-mailed link.</param>
    /// <returns>The page.</returns>
    [HttpGet("Cancel")]
    [Produces(MediaTypeNames.Text.Html)]
    public async Task<ActionResult> CancelPage([FromQuery] string? t)
        => string.IsNullOrWhiteSpace(t) ? Page("message") : Page("cancel", await _email.PeekLanguageAsync(t, TokenPurpose.CancelEmailChange).ConfigureAwait(false));

    /// <summary>
    /// Shows the account creation page of an invitation. The invitation is not consumed.
    /// </summary>
    /// <param name="t">The token of the invitation link.</param>
    /// <returns>The page.</returns>
    [HttpGet("Accept")]
    [Produces(MediaTypeNames.Text.Html)]
    public async Task<ActionResult> AcceptPage([FromQuery] string? t)
    {
        var view = string.IsNullOrWhiteSpace(t) ? null : await _invitations.PeekAsync(t, ClientKey()).ConfigureAwait(false);
        if (view is null)
        {
            return Page("acceptinvalid");
        }

        // The language the administrator chose for the invitation, the browser's only as a fallback.
        var language = Languages.IsSupported(view.Language) ? view.Language : _renderer.PickLanguage(Request.Headers.AcceptLanguage.ToString());
        var result = _renderer.Render("accept", language, new { username = view.ProposedUsername, email = view.Email ?? string.Empty });
        SecurityHeaders.Apply(Response, result.Nonce);
        return result;
    }

    /// <summary>
    /// Tells whether a user name can be chosen for an invitation.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>200 with <c>available</c>, which is false too when the link is not valid or the checks are used up.</returns>
    [HttpPost("Accept/Check")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> AcceptCheck([FromBody] AcceptCheckRequest? request)
    {
        _guard.Observe(HttpContext);
        var available = request is not null
            && await _invitations.IsUsernameAvailableAsync(request.Token ?? string.Empty, request.Username ?? string.Empty, ClientKey()).ConfigureAwait(false);
        return JsonResult(StatusCodes.Status200OK, new Dictionary<string, bool> { ["available"] = available });
    }

    /// <summary>
    /// Creates the account of an invitation.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>204, or 400.</returns>
    [HttpPost("Accept")]
    [RequestSizeLimit(MaxAcceptBodyBytes)]
    public async Task<ActionResult> Accept([FromBody] AcceptRequest? request)
    {
        _guard.Observe(HttpContext);
        var language = Languages.Parse(request?.Language) ?? Language();
        if (request is null)
        {
            return Failure(EmailTemplates.GetString(language, "page.accept.error.invalid"));
        }

        // Checked before anything is consumed.
        if (!RecoveryService.IsAcceptablePassword(request.Password))
        {
            return Failure(EmailTemplates.GetString(language, "password.tooShort"));
        }

        AcceptResult result;
        try
        {
            result = await _invitations.AcceptAsync(
                request.Token ?? string.Empty,
                request.Username ?? string.Empty,
                request.Email ?? string.Empty,
                request.Password!,
                request.Language ?? string.Empty,
                ClientKey(),
                request.Avatar).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The service logged it and undid everything.
            return JsonResult(StatusCodes.Status500InternalServerError, new Dictionary<string, string> { ["message"] = EmailTemplates.GetString(language, "page.error.network") });
        }

        var key = result switch
        {
            AcceptResult.Ok => null,
            AcceptResult.UsernameTaken => "page.accept.error.taken",
            AcceptResult.InvalidUsername => "page.accept.error.username",
            AcceptResult.InvalidEmail => "page.accept.error.email",
            AcceptResult.WeakPassword => "password.tooShort",
            _ => "page.accept.error.invalid",
        };
        return key is null ? Done() : Failure(EmailTemplates.GetString(language, key));
    }

    /// <summary>
    /// Exchanges the token of the e-mailed link for a reset session.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The session, or 400.</returns>
    [HttpPost("Reset/Link")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> ResetLink([FromBody] LinkRequest? request)
    {
        _guard.Observe(HttpContext);
        var session = request is null ? null : await _recovery.RedeemLinkAsync(request.Token ?? string.Empty, ClientKey()).ConfigureAwait(false);
        return session is null ? Failure(Invalid()) : SessionResult(session);
    }

    /// <summary>
    /// Exchanges the e-mailed PIN for a reset session. Used by the script injected in the sign-in page.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The session, or 400 with a message that does not tell what was wrong.</returns>
    [HttpPost("Reset/Pin")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> ResetPin([FromBody] PinRequest? request)
    {
        _guard.Observe(HttpContext);
        var session = request is null
            ? null
            : await _recovery.RedeemPinAsync(request.Username ?? string.Empty, request.Pin ?? string.Empty, ClientKey()).ConfigureAwait(false);
        return session is null ? Failure(Invalid()) : SessionResult(session);
    }

    /// <summary>
    /// Sets the new password for a reset session.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>204, or 400.</returns>
    [HttpPost("Reset/Complete")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> ResetComplete([FromBody] CompleteRequest? request)
    {
        _guard.Observe(HttpContext);
        if (request is null)
        {
            return Failure(Invalid());
        }

        // Checked before the session is touched, so that a too short password does not burn the session.
        if (!RecoveryService.IsAcceptablePassword(request.Password))
        {
            return Failure(EmailTemplates.GetString(Language(), "password.tooShort"));
        }

        var done = await _recovery.CompleteAsync(request.Session ?? string.Empty, request.Password!, ClientKey()).ConfigureAwait(false);
        if (!done)
        {
            return Failure(Invalid());
        }

        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    /// <summary>
    /// Confirms a new e-mail address. Used by the confirmation page.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>204, or 400.</returns>
    [HttpPost("Confirm")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> Confirm([FromBody] LinkRequest? request)
    {
        _guard.Observe(HttpContext);
        var done = request is not null && await _email.ConfirmAsync(request.Token ?? string.Empty, ClientKey()).ConfigureAwait(false);
        return done ? Done() : Failure(Invalid());
    }

    /// <summary>
    /// Cancels an e-mail change. Used by the cancellation page, which asks for the current password.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>204, 400 (invalid link), 403 (wrong password) or 429 (too many wrong passwords).</returns>
    [HttpPost("Cancel")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult> Cancel([FromBody] CancelRequest? request)
    {
        _guard.Observe(HttpContext);
        var ip = HttpContext.Connection.RemoteIpAddress;
        var remote = ip is null ? string.Empty : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        var result = request is null
            ? CancelResult.Invalid
            : await _email.CancelChangeAsync(request.Token ?? string.Empty, request.Password ?? string.Empty, ClientKey(), remote).ConfigureAwait(false);
        return result switch
        {
            CancelResult.Ok => Done(),
            CancelResult.WrongPassword => JsonResult(StatusCodes.Status403Forbidden, new Dictionary<string, string> { ["message"] = EmailTemplates.GetString(Language(), "page.cancel.error.wrongPassword") }),
            CancelResult.Locked => JsonResult(StatusCodes.Status429TooManyRequests, new Dictionary<string, string> { ["message"] = EmailTemplates.GetString(Language(), "page.cancel.error.locked") }),
            _ => Failure(Invalid()),
        };
    }

    /// <summary>
    /// Serves a script, style sheet or image of the pages.
    /// </summary>
    /// <param name="file">The file name, from a fixed list.</param>
    /// <returns>The file, or 404.</returns>
    [HttpGet("assets/{file}")]
    public ActionResult Assets(string file)
    {
        if (file is "logo.png" or "form-logo.png")
        {
            // The e-mail badge logo and the form logo of the pages, both transparent PNGs.
            var logo = PageRenderer.ReadResourceBytes(file);
            if (logo is null)
            {
                return NotFound();
            }

            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.CacheControl = "public, max-age=300";
            return File(logo, "image/png");
        }

        var type = file switch
        {
            "pages.css" => "text/css; charset=utf-8",
            "pages.js" => "text/javascript; charset=utf-8",
            _ => null,
        };
        var content = type is null ? null : PageRenderer.ReadResource(file);
        if (type is null || content is null)
        {
            return NotFound();
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "public, max-age=300";
        return Content(content, type, Encoding.UTF8);
    }

    private ActionResult Page(string page, string? pageLanguage = null)
    {
        var result = _renderer.Render(page, Languages.Parse(pageLanguage) ?? Language(), new { });
        SecurityHeaders.Apply(Response, result.Nonce);
        return result;
    }

    private string Language() => _renderer.PickLanguage(Request.Headers.AcceptLanguage.ToString());

    private string Invalid() => EmailTemplates.GetString(Language(), "page.error.invalid");

    private string ClientKey()
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        return ip is null ? UnknownClientKey : ClientIp.Key(ip);
    }

    private ActionResult Done()
    {
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    private ContentResult SessionResult(string session) => JsonResult(StatusCodes.Status200OK, new Dictionary<string, string> { ["session"] = session });

    private ContentResult Failure(string message) => JsonResult(StatusCodes.Status400BadRequest, new Dictionary<string, string> { ["message"] = message });

    private ContentResult JsonResult(int status, object body)
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
