using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>The outcome of an e-mail change request.</summary>
public enum ChangeResult
{
    /// <summary>The confirmation e-mail was queued.</summary>
    Ok,

    /// <summary>The current password is wrong.</summary>
    WrongPassword,

    /// <summary>The address is not valid, or is the current address.</summary>
    InvalidEmail,

    /// <summary>Too many change requests for this user.</summary>
    RateLimited,

    /// <summary>E-mail sending is not configured, so a confirmation cannot be sent.</summary>
    Unavailable,

    /// <summary>Too many wrong passwords for this user lately: the password is not checked until the hour is over.</summary>
    PasswordLocked
}

/// <summary>The outcome of the cancellation of an e-mail change.</summary>
public enum CancelResult
{
    /// <summary>The previous address was restored.</summary>
    Ok,

    /// <summary>The link is unknown, expired or already used.</summary>
    Invalid,

    /// <summary>The password is wrong.</summary>
    WrongPassword,

    /// <summary>Too many wrong passwords lately for this user: the password is not checked.</summary>
    Locked
}

/// <summary>The outcome of a password check done under the per-user gate.</summary>
internal enum PasswordCheck
{
    /// <summary>The password is right.</summary>
    Ok,

    /// <summary>The password is wrong.</summary>
    Wrong,

    /// <summary>Too many wrong passwords: Jellyfin was not asked.</summary>
    Locked,
}

/// <summary>
/// The e-mail address of a user: change with confirmation by the user, direct change by an administrator,
/// and cancellation of a change from the old address.
/// </summary>
/// <remarks>
/// Decisions: a change request is limited to <see cref="MaxChangeRequestsPerHour"/> per user per hour (counted before the
/// password is checked, which also bounds password guessing through this endpoint). An address already confirmed by
/// another user is accepted: confirmation proves ownership and recovery is by user name, so a shared address is harmless,
/// and refusing it would let a user probe which addresses are in use.
/// </remarks>
public sealed partial class EmailService : IEventConsumer<UserDeletedEventArgs>
{
    /// <summary>
    /// The number of change requests a user can make per hour.
    /// </summary>
    public const int MaxChangeRequestsPerHour = 5;

    /// <summary>
    /// The wrong passwords a user can type per hour in Mail Concierge forms. Kept below the lockout threshold of Jellyfin, so a stolen
    /// session token cannot be used to lock the account by guessing here.
    /// </summary>
    public const int MaxPasswordFailuresPerHour = 2;

    private const int FailuresBeforeBlock = 10;
    private const int MaxAddressLength = 254;

    private static readonly TimeSpan ChangeWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan PasswordFailWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailBlock = TimeSpan.FromMinutes(15);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _passwordGates = new();
    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly RateLimiter _limiter;
    private readonly EmailQueue _queue;
    private readonly IUserManager _users;
    private readonly SessionCloser _sessionCloser;
    private readonly ISeerrSync _seerr;
    private readonly IServerApplicationHost _host;
    private readonly Func<PluginConfiguration> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailService"/> class.
    /// </summary>
    /// <param name="store">The data store.</param>
    /// <param name="tokens">The token service.</param>
    /// <param name="limiter">The rate limiter.</param>
    /// <param name="queue">The e-mail queue.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="sessionCloser">The session closer.</param>
    /// <param name="seerr">The Seerr synchronisation.</param>
    /// <param name="host">The application host (server name).</param>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="time">The time source.</param>
    /// <param name="logger">The logger.</param>
    public EmailService(
        MailConciergeStore store,
        TokenService tokens,
        RateLimiter limiter,
        EmailQueue queue,
        IUserManager users,
        SessionCloser sessionCloser,
        ISeerrSync seerr,
        IServerApplicationHost host,
        Func<PluginConfiguration> config,
        TimeProvider time,
        ILogger<EmailService> logger)
    {
        _store = store;
        _tokens = tokens;
        _limiter = limiter;
        _queue = queue;
        _users = users;
        _sessionCloser = sessionCloser;
        _seerr = seerr;
        _host = host;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Normalizes and validates an address: ASCII dot-atom local part, host with at least one dot, no IP literal.
    /// </summary>
    /// <param name="input">The typed address.</param>
    /// <param name="normalized">The trimmed address when valid.</param>
    /// <returns>True when the address is a plain <c>local@domain</c> address.</returns>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        var candidate = input?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxAddressLength
            || !PlainAddress().IsMatch(candidate)
            || !MailAddress.TryCreate(candidate, out var parsed)
            || !string.Equals(parsed.Address, candidate, StringComparison.Ordinal)
            || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    // Plain ASCII dot-atom local part (at most 64 characters, no quotes, spaces or comments) and a host of
    // letter/digit/hyphen labels with at least one dot; an IP literal such as [1.2.3.4] cannot match.
    [GeneratedRegex(@"^(?=[^@]{1,64}@)[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+(?=[A-Za-z0-9-]*[A-Za-z])[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainAddress();

    /// <summary>
    /// Gets the e-mail settings of a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>A copy of the record, or null when the user has none.</returns>
    public Task<UserEmail?> GetAsync(Guid userId)
        => _store.ReadAsync(d => d.Emails.FirstOrDefault(e => e.UserId == userId));

    /// <summary>
    /// Lists the e-mail settings of all users that have a record.
    /// </summary>
    /// <returns>Copies of the records.</returns>
    public Task<IReadOnlyList<UserEmail>> ListAsync()
        => _store.ReadAsync(d => (IReadOnlyList<UserEmail>)d.Emails.ToList());

    /// <summary>
    /// Starts an e-mail change: checks the password, stores the pending address and sends a confirmation to it.
    /// The current address stays active until the confirmation.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="newEmail">The new address.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="remoteEndPoint">The client address, for the authentication.</param>
    /// <returns>The outcome.</returns>
    public async Task<ChangeResult> RequestChangeAsync(Guid userId, string newEmail, string currentPassword, string remoteEndPoint)
    {
        var cfg = _config();
        if (!cfg.CanSendEmail())
        {
            return ChangeResult.Unavailable;
        }

        if (!TryNormalize(newEmail, out var address) || _users.GetUserById(userId) is not { } user)
        {
            return ChangeResult.InvalidEmail;
        }

        var current = await GetAsync(userId).ConfigureAwait(false);
        if (string.Equals(current?.Email, address, StringComparison.OrdinalIgnoreCase))
        {
            // Nothing to change: does not use a rate-limit slot.
            return ChangeResult.InvalidEmail;
        }

        if (_limiter.IsBlocked(RateLimitBuckets.PasswordFailUser, userId.ToString("N")))
        {
            return ChangeResult.PasswordLocked;
        }

        if (!_limiter.TryAcquire(RateLimitBuckets.EmailChangeUser, userId.ToString("N"), MaxChangeRequestsPerHour, ChangeWindow))
        {
            return ChangeResult.RateLimited;
        }

        switch (await CheckPasswordAsync(userId, user.Username, currentPassword, remoteEndPoint).ConfigureAwait(false))
        {
            case PasswordCheck.Locked:
                return ChangeResult.PasswordLocked;
            case PasswordCheck.Wrong:
                return ChangeResult.WrongPassword;
        }

        var now = Now();
        var expires = now.AddHours(cfg.ConfirmationHours);
        var lang = Languages.ForAccounts(cfg.DefaultLanguage);
        var token = _tokens.NewToken();
        var same = false;
        await _store.WriteAsync(d =>
        {
            d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
            var index = d.Emails.FindIndex(e => e.UserId == userId);
            var current = index >= 0 ? d.Emails[index] : new UserEmail(userId, null, null, lang, null);
            if (string.Equals(current.Email, address, StringComparison.OrdinalIgnoreCase))
            {
                same = true;
                return;
            }

            var updated = current with { PendingEmail = address };
            if (index >= 0)
            {
                d.Emails[index] = updated;
            }
            else
            {
                d.Emails.Add(updated);
            }

            d.Tokens.RemoveAll(t => t.UserId == userId && t.Purpose == TokenPurpose.ConfirmEmail);
            d.Tokens.Add(new TokenRecord(_tokens.Hash(token), TokenPurpose.ConfirmEmail, userId, null, address, expires, 0));
        }).ConfigureAwait(false);

        if (same)
        {
            return ChangeResult.InvalidEmail;
        }

        SendConfirmation(user.Username, userId, address, lang, token, expires, cfg);
        return ChangeResult.Ok;
    }

    /// <summary>
    /// Stores an address typed by a new account holder as pending and sends the confirmation to it.
    /// No password is checked: the account was just created by the caller. The address stays unusable until confirmed.
    /// </summary>
    /// <param name="user">The new user.</param>
    /// <param name="address">The address, already normalized.</param>
    /// <param name="language">The language of the e-mails; anything but fr and en means the default language.</param>
    /// <returns>A task that completes when the address was stored and the confirmation queued.</returns>
    public async Task SetPendingForNewAccountAsync(User user, string address, string language)
    {
        ArgumentNullException.ThrowIfNull(user);
        var cfg = _config();
        var now = Now();
        var expires = now.AddHours(cfg.ConfirmationHours);
        var lang = Languages.Normalize(language, cfg.DefaultLanguage);
        var token = _tokens.NewToken();
        await _store.WriteAsync(d =>
        {
            d.Emails.RemoveAll(e => e.UserId == user.Id);
            d.Emails.Add(new UserEmail(user.Id, null, address, lang, null));
            d.Tokens.RemoveAll(t => t.ExpiresAt <= now || (t.UserId == user.Id && t.Purpose == TokenPurpose.ConfirmEmail));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(token), TokenPurpose.ConfirmEmail, user.Id, null, address, expires, 0));
        }).ConfigureAwait(false);

        if (cfg.CanSendEmail())
        {
            SendConfirmation(user.Username, user.Id, address, lang, token, expires, cfg);
        }
        else
        {
            _logger.LogWarning("E-mail sending is not configured: the confirmation for the new account {UserId} was not sent", user.Id);
        }
    }

    /// <summary>
    /// Gets the language of an e-mail change from the token of its confirmation or cancellation link. Nothing is consumed.
    /// </summary>
    /// <param name="token">The token of the e-mailed link.</param>
    /// <param name="purpose">The purpose of the token: <see cref="TokenPurpose.ConfirmEmail"/> or <see cref="TokenPurpose.CancelEmailChange"/>.</param>
    /// <returns>The Mail Concierge default language, or null when the link is unknown or expired.</returns>
    public async Task<string?> PeekLanguageAsync(string token, TokenPurpose purpose)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var now = Now();
            var candidate = token.Trim();
            var language = Languages.ForAccounts(_config().DefaultLanguage);
            return await _store.ReadAsync(d =>
            {
                var found = d.Tokens.FirstOrDefault(t => t.Purpose == purpose && t.UserId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash));
                return found is null ? null : language;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "E-mail change language lookup failed");
            return null;
        }
    }

    /// <summary>
    /// Confirms a pending address. Call it from the POST handler only.
    /// </summary>
    /// <param name="token">The token of the e-mailed link.</param>
    /// <param name="clientIpKey">The client IP key, fed to the token-failure limiter.</param>
    /// <returns>True when the address was confirmed.</returns>
    public async Task<bool> ConfirmAsync(string token, string clientIpKey = "unknown")
    {
        try
        {
            if (_limiter.IsBlocked(RateLimitBuckets.TokenFailIp, clientIpKey))
            {
                return false;
            }

            var cfg = _config();
            var now = Now();
            var cancelToken = _tokens.NewToken();
            var cancelExpires = now.AddHours(cfg.CancelEmailChangeHours);
            Guid? confirmedUser = null;
            string? oldAddress = null;
            if (!string.IsNullOrWhiteSpace(token))
            {
                var candidate = token.Trim();

                // Read first: an unknown token must not cost a write (serialisation and fsync under the global lock).
                var known = await _store.ReadAsync(d => d.Tokens.Any(t => t.Purpose == TokenPurpose.ConfirmEmail && t.UserId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash))).ConfigureAwait(false);
                if (known)
                {
                    await _store.WriteAsync(d =>
                    {
                        d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
                        var found = d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.ConfirmEmail && t.UserId is not null && _tokens.Matches(candidate, t.Hash));
                        if (found?.UserId is not Guid userId)
                        {
                            return;
                        }

                        var index = d.Emails.FindIndex(e => e.UserId == userId);
                        var current = index >= 0 ? d.Emails[index] : null;
                        d.Tokens.Remove(found);
                        if (current?.PendingEmail is not { } pending || !string.Equals(pending, found.Payload, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        d.Emails[index] = current with { Email = pending, PendingEmail = null, ConfirmedAt = now };

                        // Earlier cancel links are kept: each restores its own old address, so a chain of changes cannot kill the owner's link.
                        d.Tokens.RemoveAll(t => t.UserId == userId && t.Purpose is TokenPurpose.ConfirmEmail or TokenPurpose.ResetLink or TokenPurpose.ResetPin or TokenPurpose.ResetSession);
                        confirmedUser = userId;
                        if (!string.IsNullOrWhiteSpace(current.Email) && !string.Equals(current.Email, pending, StringComparison.OrdinalIgnoreCase))
                        {
                            oldAddress = current.Email;
                            d.Tokens.Add(new TokenRecord(_tokens.Hash(cancelToken), TokenPurpose.CancelEmailChange, userId, null, oldAddress, cancelExpires, 0));
                        }
                    }).ConfigureAwait(false);
                }
            }

            if (confirmedUser is not Guid id)
            {
                RecordIpFailure(clientIpKey);
                return false;
            }

            try
            {
                if (oldAddress is not null && _users.GetUserById(id) is { } user)
                {
                    var lang = Languages.ForAccounts(cfg.DefaultLanguage);
                    var values = new Dictionary<string, string>
                    {
                        ["username"] = user.Username,
                        ["serverName"] = ServerName(),
                        ["cancelLink"] = cfg.PublicUrl.TrimEnd('/') + "/MailConcierge/Cancel?t=" + cancelToken,
                        ["expires"] = Languages.FormatDateTime(cancelExpires, _time.LocalTimeZone, lang),
                    };
                    var (subject, html, text) = EmailTemplates.Render("email-changed", lang, values, cfg.TemplateOverrides);
                    _queue.Enqueue(new EmailMessage(oldAddress, subject, html, text, "email-changed", id));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not send the e-mail change warning for user {UserId}", id);
            }

            await PushToSeerr(id).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "E-mail confirmation failed");
            return false;
        }
    }

    /// <summary>
    /// Cancels an e-mail change from the link sent to the old address: the old address is restored as confirmed, once the
    /// current password of the account has been given. The address that was replaced gets a notice, the sessions of the
    /// account are closed according to the session policy and pending reset tokens are invalidated.
    /// The old address is restored even if another user has confirmed it since. Call it from the POST handler only.
    /// </summary>
    /// <param name="token">The token of the e-mailed link.</param>
    /// <param name="currentPassword">The current password of the account.</param>
    /// <param name="clientIpKey">The client IP key, fed to the failure limiter.</param>
    /// <param name="remoteEndPoint">The client address, for the authentication.</param>
    /// <returns>The outcome.</returns>
    public async Task<CancelResult> CancelChangeAsync(string token, string currentPassword, string clientIpKey = "unknown", string remoteEndPoint = "")
    {
        try
        {
            if (_limiter.IsBlocked(RateLimitBuckets.TokenFailIp, clientIpKey))
            {
                return CancelResult.Invalid;
            }

            var now = Now();
            var candidate = (token ?? string.Empty).Trim();
            var owner = string.IsNullOrEmpty(candidate)
                ? null
                : await _store.ReadAsync(d => d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.CancelEmailChange && t.UserId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash))?.UserId).ConfigureAwait(false);
            if (owner is not Guid userId || _users.GetUserById(userId) is not { } user)
            {
                RecordIpFailure(clientIpKey);
                return CancelResult.Invalid;
            }

            // Same limit as the e-mail change form: Jellyfin must never get enough wrong passwords to lock the account.
            if (_limiter.IsBlocked(RateLimitBuckets.PasswordFailUser, userId.ToString("N")))
            {
                return CancelResult.Locked;
            }

            switch (await CheckPasswordAsync(userId, user.Username, currentPassword, remoteEndPoint).ConfigureAwait(false))
            {
                case PasswordCheck.Locked:
                    return CancelResult.Locked;
                case PasswordCheck.Wrong:
                    RecordIpFailure(clientIpKey);
                    return CancelResult.WrongPassword;
            }

            string? replaced = null;
            var restored = false;
            await _store.WriteAsync(d =>
            {
                d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
                var found = d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.CancelEmailChange && t.UserId == userId && _tokens.Matches(candidate, t.Hash));
                if (found is null || string.IsNullOrWhiteSpace(found.Payload))
                {
                    return;
                }

                var index = d.Emails.FindIndex(e => e.UserId == userId);
                var current = index >= 0 ? d.Emails[index] : new UserEmail(userId, null, null, _config().DefaultLanguage, null);
                replaced = current.Email;
                var updated = current with { Email = found.Payload, PendingEmail = null, ConfirmedAt = now };
                if (index >= 0)
                {
                    d.Emails[index] = updated;
                }
                else
                {
                    d.Emails.Add(updated);
                }

                d.Tokens.RemoveAll(t => t.UserId == userId && t.Purpose is TokenPurpose.ConfirmEmail or TokenPurpose.CancelEmailChange or TokenPurpose.ResetLink or TokenPurpose.ResetPin or TokenPurpose.ResetSession);
                restored = true;
                if (string.Equals(replaced, found.Payload, StringComparison.OrdinalIgnoreCase))
                {
                    replaced = null;
                }
            }).ConfigureAwait(false);

            if (!restored)
            {
                RecordIpFailure(clientIpKey);
                return CancelResult.Invalid;
            }

            await NotifyCancellation(user, userId, replaced).ConfigureAwait(false);
            await PushToSeerr(userId).ConfigureAwait(false);
            return CancelResult.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "E-mail change cancellation failed");
            return CancelResult.Invalid;
        }
    }

    /// <summary>
    /// Sets the address of a user as an administrator: confirmed immediately, nothing is sent. A blank address removes it.
    /// Pending confirmations and reset tokens of the user are invalidated.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="email">The address, or null or blank to remove it.</param>
    /// <returns>A task that completes when the address was stored.</returns>
    /// <exception cref="ArgumentException">The address is not valid.</exception>
    public async Task SetByAdminAsync(Guid userId, string? email)
    {
        string? address = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            if (!TryNormalize(email, out var normalized))
            {
                throw new ArgumentException("The e-mail address is not valid.", nameof(email));
            }

            address = normalized;
        }

        var cfg = _config();
        var now = Now();
        await _store.WriteAsync(d =>
        {
            var index = d.Emails.FindIndex(e => e.UserId == userId);

            // The stored language is no longer read for e-mails; the old value is kept, the default fills a new record.
            var lang = index >= 0 ? d.Emails[index].Language : Languages.ForAccounts(cfg.DefaultLanguage);
            var updated = new UserEmail(userId, address, null, lang, address is null ? null : now);
            if (index >= 0)
            {
                d.Emails[index] = updated;
            }
            else
            {
                d.Emails.Add(updated);
            }

            d.Tokens.RemoveAll(t => t.UserId == userId && t.Purpose is TokenPurpose.ConfirmEmail or TokenPurpose.CancelEmailChange or TokenPurpose.ResetLink or TokenPurpose.ResetPin or TokenPurpose.ResetSession);
        }).ConfigureAwait(false);
        await PushToSeerr(userId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        var userId = eventArgs.Argument.Id;
        return _store.WriteAsync(d =>
        {
            d.Emails.RemoveAll(e => e.UserId == userId);
            d.Tokens.RemoveAll(t => t.UserId == userId);
        });
    }

    private void SendConfirmation(string username, Guid userId, string address, string lang, string token, DateTime expires, PluginConfiguration cfg)
    {
        var values = new Dictionary<string, string>
        {
            ["username"] = username,
            ["serverName"] = ServerName(),
            ["link"] = cfg.PublicUrl.TrimEnd('/') + "/MailConcierge/Confirm?t=" + token,
            ["expires"] = FormatDate(expires, lang),
        };
        var (subject, html, text) = EmailTemplates.Render("confirm-email", lang, values, cfg.TemplateOverrides);
        _queue.Enqueue(new EmailMessage(address, subject, html, text, "confirm-email", userId));
    }

    private string FormatDate(DateTime utc, string language)
        => Languages.FormatDate(utc, _time.LocalTimeZone, language);

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private string ServerName() => Languages.ServerName(_host);

    /// <summary>
    /// Checks the password of a user while holding a per-user gate, so that parallel requests cannot all pass the "too many wrong
    /// passwords" test before the first failure is counted: at most the allowed number of wrong passwords reach Jellyfin.
    /// </summary>
    private async Task<PasswordCheck> CheckPasswordAsync(Guid userId, string username, string password, string remoteEndPoint)
    {
        var gate = _passwordGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_limiter.IsBlocked(RateLimitBuckets.PasswordFailUser, userId.ToString("N")))
            {
                return PasswordCheck.Locked;
            }

            if (await PasswordIsValid(username, password, remoteEndPoint).ConfigureAwait(false))
            {
                return PasswordCheck.Ok;
            }

            RecordPasswordFailure(userId);
            return PasswordCheck.Wrong;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> PasswordIsValid(string username, string password, string remoteEndPoint)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            return await _users.AuthenticateUser(username, password, remoteEndPoint, false).ConfigureAwait(false) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Password check refused for an e-mail change");
            return false;
        }
    }

    private async Task PushToSeerr(Guid userId)
    {
        try
        {
            await _seerr.PushAsync(userId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push the e-mail of user {UserId} to Seerr", userId);
        }
    }

    private async Task NotifyCancellation(User user, Guid userId, string? replacedAddress)
    {
        var cfg = _config();
        try
        {
            if (!string.IsNullOrWhiteSpace(replacedAddress) && cfg.CanSendEmail())
            {
                var lang = Languages.ForAccounts(cfg.DefaultLanguage);
                var values = new Dictionary<string, string> { ["username"] = user.Username, ["serverName"] = ServerName() };
                var (subject, html, text) = EmailTemplates.Render("email-change-cancelled", lang, values, cfg.TemplateOverrides);
                _queue.Enqueue(new EmailMessage(replacedAddress, subject, html, text, "email-change-cancelled", userId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send the cancellation notice for user {UserId}", userId);
        }

        try
        {
            await _sessionCloser.CloseAsync(userId, cfg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not close the sessions of user {UserId} after an e-mail change cancellation", userId);
        }
    }

    private void RecordPasswordFailure(Guid userId)
    {
        // The last allowed failure blocks the user for the rest of the hour: at most MaxPasswordFailuresPerHour reach Jellyfin.
        var key = userId.ToString("N");
        if (!_limiter.TryAcquire(RateLimitBuckets.PasswordFailUser, key, MaxPasswordFailuresPerHour - 1, PasswordFailWindow))
        {
            _limiter.Penalize(RateLimitBuckets.PasswordFailUser, key, PasswordFailWindow);
        }
    }

    private void RecordIpFailure(string clientIpKey)
    {
        if (!_limiter.TryAcquire(RateLimitBuckets.TokenFailIp, clientIpKey, FailuresBeforeBlock - 1, FailWindow))
        {
            _limiter.Penalize(RateLimitBuckets.TokenFailIp, clientIpKey, FailBlock);
        }
    }
}
