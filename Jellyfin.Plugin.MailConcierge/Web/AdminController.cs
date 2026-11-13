using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using Jellyfin.Plugin.MailConcierge.Security;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// The administration endpoints behind the Mail Concierge configuration page. Secrets are write-only.
/// </summary>
[ApiController]
[Route("MailConcierge/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AdminController : ControllerBase
{
    private const int MaxBodyBytes = 524288;
    private const int MaxTemplateChars = 65536;
    private const int MaxTextChars = 512;
    private const string SubjectPrefix = "Subject: ";
    private const int MaxSharedEntries = 20;
    private const int MaxSharedNames = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Extensions = { "html", "txt" };

    private readonly IUserManager _users;
    private readonly EmailQueue _queue;
    private readonly ProxyGuard _guard;
    private readonly ServiceGate _gate;
    private readonly EmailService _emails;
    private readonly EmailLoginProbe _probe;
    private readonly Func<PluginConfiguration> _config;
    private readonly Action<PluginConfiguration> _save;
    private readonly ILogger<AdminController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="users">The user manager.</param>
    /// <param name="queue">The e-mail queue (test e-mail).</param>
    /// <param name="guard">The proxy guard.</param>
    /// <param name="gate">Tells whether the service is open (enabled, proxy guard quiet).</param>
    /// <param name="emails">The e-mail addresses of the users (shared address count).</param>
    /// <param name="probe">Tells whether e-mail sign-in can work with this Jellyfin version.</param>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="save">Persists the configuration.</param>
    /// <param name="logger">The logger.</param>
    public AdminController(IUserManager users, EmailQueue queue, ProxyGuard guard, ServiceGate gate, EmailService emails, EmailLoginProbe probe, Func<PluginConfiguration> config, Action<PluginConfiguration> save, ILogger<AdminController> logger)
    {
        _users = users;
        _queue = queue;
        _guard = guard;
        _gate = gate;
        _emails = emails;
        _probe = probe;
        _config = config;
        _save = save;
        _logger = logger;
    }

    /// <summary>
    /// Gets the configuration, with the secrets blanked.
    /// </summary>
    /// <returns>The configuration.</returns>
    [HttpGet("Config")]
    public ActionResult GetConfig()
    {
        var c = _config();
        return Json(
            StatusCodes.Status200OK,
            new
            {
                enabled = c.Enabled,
                nativeForgotPassword = c.NativeForgotPassword,
                publicUrl = c.PublicUrl,
                defaultLanguage = c.DefaultLanguage,
                languages = Languages.Supported,
                smtpHost = c.SmtpHost,
                smtpPort = c.SmtpPort,
                smtpSecurity = c.SmtpSecurity.ToString(),
                smtpUsername = c.SmtpUsername,
                smtpPassword = string.Empty,
                smtpPasswordSet = !string.IsNullOrEmpty(c.SmtpPassword),
                fromAddress = c.FromAddress,
                fromName = c.FromName,
                pinMinutes = c.PinMinutes,
                invitationDays = c.InvitationDays,
                confirmationHours = c.ConfirmationHours,
                cancelEmailChangeHours = c.CancelEmailChangeHours,
                maxRecoveryEmailsPerAccountPerHour = c.MaxRecoveryEmailsPerAccountPerHour,
                maxForgotRequestsPerIpPer15Min = c.MaxForgotRequestsPerIpPer15Min,
                maxRecoveryEmailsPerHour = c.MaxRecoveryEmailsPerHour,
                maxInvitationEmailsPerHour = c.MaxInvitationEmailsPerHour,
                maxAccountEmailsPerHour = c.MaxAccountEmailsPerHour,
                maxPinFailures = c.MaxPinFailures,
                allowAdministrators = c.AllowAdministrators,
                emailLogin = c.EmailLogin,
                sessionsOnReset = c.SessionsOnReset.ToString(),
                sessionInactiveDays = c.SessionInactiveDays,
                seerrEnabled = c.SeerrEnabled,
                seerrUrl = c.SeerrUrl,
                seerrApiKey = string.Empty,
                seerrApiKeySet = !string.IsNullOrEmpty(c.SeerrApiKey),
                templateOverrides = c.TemplateOverrides.ToDictionary(kv => kv.Key, kv => kv.Value),
            });
    }

    /// <summary>
    /// Validates and saves the configuration. An empty secret keeps the stored one.
    /// </summary>
    /// <param name="request">The new values.</param>
    /// <returns>200 with <c>{ok}</c>, or 400 with a <c>code</c> (nothing is saved).</returns>
    [HttpPost("Config")]
    [RequestSizeLimit(MaxBodyBytes)]
    public ActionResult SetConfig([FromBody] AdminConfigRequest? request)
    {
        if (request is null)
        {
            return Code(StatusCodes.Status400BadRequest, "invalid");
        }

        var error = Validate(request, out var smtpSecurity, out var sessions);
        if (error is not null)
        {
            return Code(StatusCodes.Status400BadRequest, error);
        }

        var c = _config();
        if (SecretOfAnotherServer(request, smtpSecurity, c) is { } secretError)
        {
            return Code(StatusCodes.Status400BadRequest, secretError);
        }

        c.Enabled = request.Enabled ?? c.Enabled;
        c.NativeForgotPassword = request.NativeForgotPassword ?? c.NativeForgotPassword;
        c.PublicUrl = request.PublicUrl?.Trim() ?? c.PublicUrl;
        c.DefaultLanguage = request.DefaultLanguage ?? c.DefaultLanguage;
        c.SmtpHost = request.SmtpHost?.Trim() ?? c.SmtpHost;
        c.SmtpPort = request.SmtpPort ?? c.SmtpPort;
        c.SmtpSecurity = smtpSecurity ?? c.SmtpSecurity;
        c.SmtpUsername = request.SmtpUsername?.Trim() ?? c.SmtpUsername;
        if (!string.IsNullOrEmpty(request.SmtpPassword))
        {
            c.SmtpPassword = request.SmtpPassword;
        }
        else if (request.ClearSmtpPassword == true)
        {
            c.SmtpPassword = string.Empty;
        }

        c.FromAddress = request.FromAddress?.Trim() ?? c.FromAddress;
        c.FromName = request.FromName ?? c.FromName;
        c.PinMinutes = request.PinMinutes ?? c.PinMinutes;
        c.InvitationDays = request.InvitationDays ?? c.InvitationDays;
        c.ConfirmationHours = request.ConfirmationHours ?? c.ConfirmationHours;
        c.CancelEmailChangeHours = request.CancelEmailChangeHours ?? c.CancelEmailChangeHours;
        c.MaxRecoveryEmailsPerAccountPerHour = request.MaxRecoveryEmailsPerAccountPerHour ?? c.MaxRecoveryEmailsPerAccountPerHour;
        c.MaxForgotRequestsPerIpPer15Min = request.MaxForgotRequestsPerIpPer15Min ?? c.MaxForgotRequestsPerIpPer15Min;
        c.MaxRecoveryEmailsPerHour = request.MaxRecoveryEmailsPerHour ?? c.MaxRecoveryEmailsPerHour;
        c.MaxInvitationEmailsPerHour = request.MaxInvitationEmailsPerHour ?? c.MaxInvitationEmailsPerHour;
        c.MaxAccountEmailsPerHour = request.MaxAccountEmailsPerHour ?? c.MaxAccountEmailsPerHour;
        c.MaxPinFailures = request.MaxPinFailures ?? c.MaxPinFailures;
        c.AllowAdministrators = request.AllowAdministrators ?? c.AllowAdministrators;
        c.EmailLogin = request.EmailLogin ?? c.EmailLogin;
        c.SessionsOnReset = sessions ?? c.SessionsOnReset;
        c.SessionInactiveDays = request.SessionInactiveDays ?? c.SessionInactiveDays;
        c.SeerrEnabled = request.SeerrEnabled ?? c.SeerrEnabled;
        c.SeerrUrl = request.SeerrUrl?.Trim() ?? c.SeerrUrl;
        if (!string.IsNullOrEmpty(request.SeerrApiKey))
        {
            c.SeerrApiKey = request.SeerrApiKey;
        }

        if (request.TemplateOverrides is not null)
        {
            c.TemplateOverrides.Clear();
            foreach (var (key, value) in request.TemplateOverrides.Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && !IsShippedTemplate(kv.Key, kv.Value)))
            {
                c.TemplateOverrides[key] = value;
            }
        }

        _save(c);
        return Json(StatusCodes.Status200OK, new { ok = true });
    }

    /// <summary>
    /// Gets the templates shipped with the plugin, for "reset to default".
    /// </summary>
    /// <returns>The content by key <c>{lang}/{template}.{html|txt}</c>.</returns>
    [HttpGet("Templates")]
    public ActionResult Templates()
    {
        var result = new Dictionary<string, string>();
        foreach (var lang in Languages.Supported.Select(l => l.Code))
        {
            foreach (var name in EmailTemplates.TemplateNames)
            {
                foreach (var ext in Extensions)
                {
                    var content = EmailTemplates.ReadEmbedded(lang, name, ext);
                    if (content is not null)
                    {
                        result[$"{lang}/{name}.{ext}"] = content;
                    }
                }
            }
        }

        return Json(StatusCodes.Status200OK, result);
    }

    /// <summary>
    /// Gets the languages shipped with the plugin (code and own name), discovered from the strings files.
    /// </summary>
    /// <returns>The list <c>[{ code, name }]</c>.</returns>
    [HttpGet("Languages")]
    public ActionResult GetLanguages() => Json(StatusCodes.Status200OK, Languages.Supported);

    /// <summary>
    /// Gets the texts of an administration page in a language, English filling the missing keys.
    /// </summary>
    /// <param name="lang">The language tag; an unsupported one gives the default language.</param>
    /// <param name="page">The page: <c>config</c> or <c>invitations</c>.</param>
    /// <returns><c>{ language, strings }</c>, the keys without their page prefix.</returns>
    [HttpGet("Strings")]
    public ActionResult GetStrings([FromQuery] string? lang, [FromQuery] string? page)
    {
        var prefix = page switch
        {
            "config" => "cfg.",
            "invitations" => "inv.",
            _ => null,
        };
        if (prefix is null)
        {
            return Code(StatusCodes.Status400BadRequest, "invalid");
        }

        var language = Languages.Parse(lang) ?? Languages.Fallback;
        var strings = EmailTemplates.GetStrings(language, prefix).ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value);
        return Json(StatusCodes.Status200OK, new { language, strings });
    }

    /// <summary>
    /// Sends a test e-mail now.
    /// </summary>
    /// <param name="request">The recipient.</param>
    /// <returns><c>{ ok, error }</c>; <c>error</c> is a code (<c>test_not_configured</c>, <c>test_send_failed</c>), never the text of the failure.</returns>
    [HttpPost("TestEmail")]
    [RequestSizeLimit(MaxTextChars)]
    public async Task<ActionResult> TestEmail([FromBody] TestEmailRequest? request)
    {
        if (request?.To is null || !EmailService.TryNormalize(request.To, out var to))
        {
            return Code(StatusCodes.Status400BadRequest, "invalidEmail");
        }

        if (!_config().CanSendEmail())
        {
            return Json(StatusCodes.Status200OK, new { ok = false, error = "test_not_configured" });
        }

        // The failure text can describe the network around the server (host, port, certificate): it goes to the log, never to the browser.
        var detail = await _queue.SendTestAsync(to).ConfigureAwait(false);
        if (detail is not null)
        {
            _logger.LogWarning("The test e-mail could not be sent: {Error}", detail);
        }

        return Json(StatusCodes.Status200OK, new { ok = detail is null, error = detail is null ? null : "test_send_failed" });
    }

    /// <summary>
    /// Gives every eligible account the Mail Concierge password-reset provider.
    /// </summary>
    /// <returns>The number assigned and the number skipped (administrators, external providers).</returns>
    [HttpPost("AssignProvider")]
    public async Task<ActionResult> AssignProvider()
    {
        var allowAdmins = _config().AllowAdministrators;
        int assigned = 0, skippedAdmins = 0, skippedExternal = 0;
        foreach (var user in _users.GetUsers().ToList())
        {
            if (user.AuthenticationProviderId != AccountPolicy.DefaultAuthProviderId)
            {
                skippedExternal++;
                continue;
            }

            if (!allowAdmins && user.HasPermission(PermissionKind.IsAdministrator))
            {
                skippedAdmins++;
                continue;
            }

            if (user.PasswordResetProviderId == MailConciergePasswordResetProvider.Id)
            {
                continue;
            }

            user.PasswordResetProviderId = MailConciergePasswordResetProvider.Id;
            await _users.UpdateUserAsync(user).ConfigureAwait(false);
            assigned++;
        }

        return Json(StatusCodes.Status200OK, new { assigned, skippedAdmins, skippedExternal });
    }

    /// <summary>
    /// Gets the state shown in the banner of the page.
    /// </summary>
    /// <returns>
    /// <c>{ proxyGuardTripped, reason, canSendEmail, serviceOpen, emailLoginAvailable, sharedEmailCount, sharedEmailAccounts }</c>; <c>reason</c> is a code, never a sentence;
    /// <c>sharedEmailCount</c> is the number of confirmed addresses held by several accounts, 0 when the stored addresses cannot be read;
    /// <c>sharedEmailAccounts</c> names those accounts, never the addresses: one entry per shared address, each the sorted user names of the accounts
    /// holding it (an id without an account is left out), the entries sorted by their first name, at most 20 entries of 10 names; empty when the stored addresses cannot be read.
    /// </returns>
    [HttpGet("Status")]
    public async Task<ActionResult> Status()
    {
        var shared = 0;
        var sharedAccounts = new List<string[]>();
        try
        {
            var records = await _emails.ListAsync().ConfigureAwait(false);
            var groups = records
                .Where(e => !string.IsNullOrWhiteSpace(e.Email) && e.ConfirmedAt is not null)
                .GroupBy(e => e.Email!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Select(e => e.UserId).Distinct().ToList())
                .Where(ids => ids.Count > 1)
                .ToList();
            var entries = groups
                .Select(ids => ids
                    .Select(id => _users.GetUserById(id)?.Username)
                    .OfType<string>()
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name, StringComparer.Ordinal)
                    .ToArray())
                .Where(names => names.Length > 0)
                .OrderBy(names => names[0], StringComparer.OrdinalIgnoreCase)
                .ThenBy(names => names[0], StringComparer.Ordinal)
                .Take(MaxSharedEntries)
                .Select(names => names.Take(MaxSharedNames).ToArray())
                .ToList();
            shared = groups.Count;
            sharedAccounts = entries;
        }
        catch (Exception ex)
        {
            // The rest of the banner stays readable. The exception itself is not logged: its message could quote the stored data.
            _logger.LogError("The shared addresses could not be counted: {ExceptionType}", ex.GetType().FullName);
        }

        return Json(
            StatusCodes.Status200OK,
            new
            {
                proxyGuardTripped = _guard.Tripped,
                reason = _guard.ReasonCode,
                canSendEmail = _config().CanSendEmail(),
                serviceOpen = _gate.IsOpen,
                emailLoginAvailable = _probe.IsAvailable,
                sharedEmailCount = shared,
                sharedEmailAccounts = sharedAccounts,
            });
    }

    // A stored secret is never sent to another server, or without encryption, without being typed again: an empty password or key field
    // means "keep", which is only safe while the server it belongs to and the way it is sent are the same. Nothing is stored when this returns a code.
    private static string? SecretOfAnotherServer(AdminConfigRequest r, SmtpSecurity? smtpSecurity, PluginConfiguration c)
    {
        if (r.SmtpHost is not null && !string.IsNullOrEmpty(c.SmtpPassword) && string.IsNullOrEmpty(r.SmtpPassword) && r.ClearSmtpPassword != true
            && !string.Equals(r.SmtpHost.Trim(), c.SmtpHost?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "smtp_server_changed";
        }

        // Turning encryption off would send the stored password in clear to the same host.
        if (smtpSecurity == SmtpSecurity.None && c.SmtpSecurity != SmtpSecurity.None
            && !string.IsNullOrEmpty(c.SmtpPassword) && string.IsNullOrEmpty(r.SmtpPassword) && r.ClearSmtpPassword != true)
        {
            return "smtp_encryption_off";
        }

        if (r.SeerrUrl is not null && !string.IsNullOrEmpty(c.SeerrApiKey) && string.IsNullOrEmpty(r.SeerrApiKey)
            && ServerOf(r.SeerrUrl).Length > 0 && !string.Equals(ServerOf(r.SeerrUrl), ServerOf(c.SeerrUrl), StringComparison.Ordinal))
        {
            return "seerr_address_changed";
        }

        return null;
    }

    // Scheme, host and port of an address, in lower case; the path does not name another server. Empty when there is none.
    private static string ServerOf(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant() : string.Empty;

    private static string? Validate(AdminConfigRequest r, out SmtpSecurity? smtpSecurity, out SessionClosePolicy? sessions)
    {
        smtpSecurity = null;
        sessions = null;

        if (r.SmtpPort is < 1 or > 65535)
        {
            return "invalid_smtp_port";
        }

        var positives = new[]
        {
            r.PinMinutes, r.InvitationDays, r.ConfirmationHours, r.CancelEmailChangeHours, r.MaxRecoveryEmailsPerAccountPerHour,
            r.MaxForgotRequestsPerIpPer15Min, r.MaxRecoveryEmailsPerHour, r.MaxInvitationEmailsPerHour, r.MaxAccountEmailsPerHour, r.MaxPinFailures, r.SessionInactiveDays,
        };
        if (positives.Any(v => v is < 1 or > 100000))
        {
            return "invalid_number";
        }

        if (r.PublicUrl is not null && !IsEmptyOrHttpUrl(r.PublicUrl))
        {
            return "invalid_public_url";
        }

        if (r.SeerrUrl is not null && !IsEmptyOrHttpUrl(r.SeerrUrl))
        {
            return "invalid_seerr_url";
        }

        if (r.DefaultLanguage is not null && !Languages.IsSupported(r.DefaultLanguage))
        {
            return "invalid_language";
        }

        if (r.SmtpSecurity is not null)
        {
            if (!Enum.TryParse<SmtpSecurity>(r.SmtpSecurity, true, out var s) || !Enum.IsDefined(s))
            {
                return "invalid_smtp_security";
            }

            smtpSecurity = s;
        }

        if (r.SessionsOnReset is not null)
        {
            if (!Enum.TryParse<SessionClosePolicy>(r.SessionsOnReset, true, out var p) || !Enum.IsDefined(p))
            {
                return "invalid_sessions";
            }

            sessions = p;
        }

        var texts = new[] { r.SmtpHost, r.SmtpUsername, r.SmtpPassword, r.FromAddress, r.FromName, r.PublicUrl, r.SeerrUrl, r.SeerrApiKey };
        if (texts.Any(t => t is not null && t.Length > MaxTextChars))
        {
            return "invalid_text";
        }

        if (r.FromAddress is { Length: > 0 } from && !string.IsNullOrWhiteSpace(from) && !EmailService.TryNormalize(from, out _))
        {
            return "invalid_from_address";
        }

        if (r.TemplateOverrides is not null)
        {
            foreach (var (key, value) in r.TemplateOverrides)
            {
                if (!IsTemplateKey(key) || (value?.Length ?? 0) > MaxTemplateChars)
                {
                    return "invalid_template";
                }

                if (key.EndsWith(".txt", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value)
                    && !value.ReplaceLineEndings("\n").StartsWith(SubjectPrefix, StringComparison.Ordinal))
                {
                    return "invalid_template_subject";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tells whether a submitted template equals the shipped one (ignoring line endings), so it is not an override.
    /// </summary>
    private static bool IsShippedTemplate(string key, string value)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        var dot = key.LastIndexOf('.');
        var shipped = EmailTemplates.ReadEmbedded(key[..slash], key[(slash + 1)..dot], key[(dot + 1)..]);
        return shipped is not null
            && string.Equals(shipped.ReplaceLineEndings("\n"), value.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private static bool IsTemplateKey(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        var dot = key.LastIndexOf('.');
        if (slash < 1 || dot < slash)
        {
            return false;
        }

        return Languages.IsSupported(key[..slash])
            && EmailTemplates.TemplateNames.Contains(key[(slash + 1)..dot])
            && Extensions.Contains(key[(dot + 1)..]);
    }

    private static bool IsEmptyOrHttpUrl(string value)
        => string.IsNullOrWhiteSpace(value)
           || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host));

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
