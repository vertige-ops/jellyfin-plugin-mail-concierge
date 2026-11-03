using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using Jellyfin.Plugin.MailConcierge.Security;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>
/// Password recovery by e-mail: a PIN and a link are sent, either one is exchanged for a short-lived reset session,
/// and the reset session is exchanged for a new password.
/// </summary>
public sealed class RecoveryService
{
    /// <summary>
    /// The minimum length of a new password.
    /// </summary>
    public const int MinPasswordLength = 8;

    // A failure that cannot get a slot blocks the IP: with 9 slots the 10th failure in the window is the one that blocks.
    private const int FailuresBeforeBlock = 10;

    private static readonly TimeSpan DefaultPinResponseFloor = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ForgotIpWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ForgotAccountWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailBlock = TimeSpan.FromMinutes(15);

    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly RateLimiter _limiter;
    private readonly EmailQueue _queue;
    private readonly ProxyGuard _guard;
    private readonly IUserManager _users;
    private readonly SessionCloser _sessionCloser;
    private readonly IServerApplicationHost _host;
    private readonly Func<PluginConfiguration> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<RecoveryService> _logger;
    private readonly TimeSpan _pinResponseFloor;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryService"/> class.
    /// </summary>
    /// <param name="store">The data store.</param>
    /// <param name="tokens">The token service.</param>
    /// <param name="limiter">The rate limiter.</param>
    /// <param name="queue">The e-mail queue.</param>
    /// <param name="guard">The reverse-proxy guard.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="sessionCloser">The session closer.</param>
    /// <param name="host">The application host (server name).</param>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="time">The time source.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pinResponseFloor">The minimum duration of a PIN redemption, so that its timing does not tell whether the account exists; defaults to 300 ms.</param>
    public RecoveryService(
        MailConciergeStore store,
        TokenService tokens,
        RateLimiter limiter,
        EmailQueue queue,
        ProxyGuard guard,
        IUserManager users,
        SessionCloser sessionCloser,
        IServerApplicationHost host,
        Func<PluginConfiguration> config,
        TimeProvider time,
        ILogger<RecoveryService> logger,
        TimeSpan? pinResponseFloor = null)
    {
        _pinResponseFloor = pinResponseFloor ?? DefaultPinResponseFloor;
        _store = store;
        _tokens = tokens;
        _limiter = limiter;
        _queue = queue;
        _guard = guard;
        _users = users;
        _sessionCloser = sessionCloser;
        _host = host;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a new password is acceptable.
    /// </summary>
    /// <param name="password">The candidate password.</param>
    /// <returns>True when long enough.</returns>
    public static bool IsAcceptablePassword(string? password) => password is not null && password.Length >= MinPasswordLength;

    /// <summary>
    /// Handles a forgot-password request. Never throws and never reveals anything: when the request is refused,
    /// rate-limited or concerns an unknown or ineligible account, nothing happens.
    /// </summary>
    /// <param name="enteredUsername">The user name typed by the visitor.</param>
    /// <param name="clientIpKey">The client IP key.</param>
    /// <returns>A task that completes when the request was handled.</returns>
    public async Task RequestAsync(string enteredUsername, string clientIpKey)
    {
        try
        {
            var cfg = _config();
            if (!cfg.Enabled || _guard.Tripped || !cfg.CanSendEmail())
            {
                _logger.LogDebug("Forgot-password request ignored: Mail Concierge is disabled, the proxy guard is tripped or e-mail is not configured");
                return;
            }

            if (!_limiter.TryAcquire(RateLimitBuckets.ForgotIp, clientIpKey, cfg.MaxForgotRequestsPerIpPer15Min, ForgotIpWindow))
            {
                _logger.LogDebug("Forgot-password request ignored: IP rate limit");
                return;
            }

            var user = string.IsNullOrWhiteSpace(enteredUsername) ? null : _users.GetUserByName(enteredUsername.Trim());
            if (user is null)
            {
                _logger.LogDebug("Forgot-password request ignored: unknown user");
                return;
            }

            var email = await _store.ReadAsync(d => d.Emails.FirstOrDefault(e => e.UserId == user.Id)).ConfigureAwait(false);
            if (!AccountPolicy.IsEligible(user, email, cfg))
            {
                _logger.LogDebug("Forgot-password request ignored: user {UserId} is not eligible", user.Id);
                return;
            }

            if (!_limiter.TryAcquire(RateLimitBuckets.ForgotAccount, user.Id.ToString("N"), cfg.MaxRecoveryEmailsPerAccountPerHour, ForgotAccountWindow))
            {
                _logger.LogDebug("Forgot-password request ignored: account rate limit for user {UserId}", user.Id);
                return;
            }

            var now = Now();
            var expires = now.AddMinutes(cfg.PinMinutes);
            var pin = _tokens.NewPin();
            var linkToken = _tokens.NewToken();
            await _store.WriteAsync(d =>
            {
                d.Tokens.RemoveAll(t => t.ExpiresAt <= now || (t.UserId == user.Id && t.Purpose is TokenPurpose.ResetPin or TokenPurpose.ResetLink));
                d.Tokens.Add(new TokenRecord(_tokens.Hash(pin), TokenPurpose.ResetPin, user.Id, null, null, expires, 0));
                d.Tokens.Add(new TokenRecord(_tokens.Hash(linkToken), TokenPurpose.ResetLink, user.Id, null, null, expires, 0));
            }).ConfigureAwait(false);

            var language = LanguageOf(cfg);
            var values = new Dictionary<string, string>
            {
                ["username"] = user.Username,
                ["serverName"] = ServerName(),
                ["pin"] = pin,
                ["link"] = cfg.PublicUrl.TrimEnd('/') + "/MailConcierge/Reset?t=" + linkToken,
                ["expires"] = Languages.FormatDateTime(expires, _time.LocalTimeZone, language),
            };
            var (subject, html, text) = EmailTemplates.Render("recovery", language, values, cfg.TemplateOverrides);
            _queue.Enqueue(new EmailMessage(email!.Email!, subject, html, text, "recovery", user.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forgot-password request failed");
        }
    }

    /// <summary>
    /// Exchanges the e-mailed PIN for a reset-session token.
    /// </summary>
    /// <param name="username">The user name the PIN belongs to.</param>
    /// <param name="pin">The PIN.</param>
    /// <param name="clientIpKey">The client IP key.</param>
    /// <returns>A reset-session token, or null when the PIN is wrong, expired or exhausted.</returns>
    public async Task<string?> RedeemPinAsync(string username, string pin, string clientIpKey)
    {
        var started = _time.GetTimestamp();
        try
        {
            var cfg = _config();
            if (!cfg.Enabled || _guard.Tripped || _limiter.IsBlocked(RateLimitBuckets.PinFailIp, clientIpKey))
            {
                return null;
            }

            var user = string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin) ? null : _users.GetUserByName(username.Trim());
            string? session = null;
            if (user is not null)
            {
                var candidate = pin.Trim();
                session = await Consume(user.Id, d => FindPin(d, user.Id), candidate, cfg.MaxPinFailures).ConfigureAwait(false);
            }

            if (session is null)
            {
                RecordIpFailure(RateLimitBuckets.PinFailIp, clientIpKey);
            }

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PIN redemption failed");
            return null;
        }
        finally
        {
            // Unknown and known accounts must take the same time to answer.
            var remaining = _pinResponseFloor - _time.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Gets the language of the account a reset link belongs to, the Mail Concierge default language, the one its e-mails are sent in. Nothing is consumed.
    /// </summary>
    /// <param name="linkToken">The token from the link.</param>
    /// <returns>A language code, or null when the link is unknown or expired.</returns>
    public async Task<string?> PeekLinkLanguageAsync(string linkToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(linkToken))
            {
                return null;
            }

            var now = Now();
            var candidate = linkToken.Trim();
            var cfg = _config();
            return await _store.ReadAsync(d =>
            {
                var link = d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.ResetLink && t.UserId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash));
                return link?.UserId is Guid userId ? LanguageOf(cfg) : null;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Link language lookup failed");
            return null;
        }
    }

    /// <summary>
    /// Tells whether <see cref="RedeemLinkAsync"/> would accept a reset link right now. Nothing is consumed and no failure is counted.
    /// </summary>
    /// <param name="linkToken">The token from the link.</param>
    /// <returns>True for an existing, unexpired, unused link of an eligible account; false otherwise, and on any error.</returns>
    public async Task<bool> IsLinkValidAsync(string linkToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(linkToken) || !_config().Enabled || _guard.Tripped)
            {
                return false;
            }

            var now = Now();
            var candidate = linkToken.Trim();
            return await _store.ReadAsync(d =>
            {
                var link = d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.ResetLink && t.UserId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash));
                return link?.UserId is Guid userId && IsEligible(d, userId);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Link validity check failed");
            return false;
        }
    }

    /// <summary>
    /// Exchanges the e-mailed link token for a reset-session token. Call it from the POST handler only.
    /// </summary>
    /// <param name="linkToken">The token from the link.</param>
    /// <param name="clientIpKey">The client IP key, fed to the token-failure limiter.</param>
    /// <returns>A reset-session token, or null when the link is unknown, expired or already used.</returns>
    public async Task<string?> RedeemLinkAsync(string linkToken, string clientIpKey)
    {
        try
        {
            if (!_config().Enabled || _guard.Tripped || _limiter.IsBlocked(RateLimitBuckets.TokenFailIp, clientIpKey))
            {
                return null;
            }

            string? session = null;
            if (!string.IsNullOrWhiteSpace(linkToken))
            {
                var now = Now();
                var candidate = linkToken.Trim();
                var sessionToken = _tokens.NewToken();

                // Read first: an unknown link must not cost a disk write.
                var known = await _store.ReadAsync(d => d.Tokens.Any(t => t.Purpose == TokenPurpose.ResetLink && t.UserId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash))).ConfigureAwait(false);
                if (known)
                {
                    await _store.WriteAsync(d =>
                    {
                        d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
                        var link = d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.ResetLink && t.UserId is not null && _tokens.Matches(candidate, t.Hash));
                        if (link?.UserId is Guid userId && IsEligible(d, userId))
                        {
                            session = IssueSession(d, userId, sessionToken, now);
                        }
                    }).ConfigureAwait(false);
                }
            }

            if (session is null)
            {
                RecordIpFailure(RateLimitBuckets.TokenFailIp, clientIpKey);
            }

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Link redemption failed");
            return null;
        }
    }

    /// <summary>
    /// Sets the new password for the holder of a reset session, ends all reset tokens and sessions, and notifies the user.
    /// </summary>
    /// <param name="sessionToken">The reset-session token. It is single use.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="clientIpKey">The client IP key, fed to the token-failure limiter.</param>
    /// <returns>True when the password was changed.</returns>
    public async Task<bool> CompleteAsync(string sessionToken, string newPassword, string clientIpKey)
    {
        try
        {
            if (!_config().Enabled || _guard.Tripped || !IsAcceptablePassword(newPassword) || string.IsNullOrWhiteSpace(sessionToken)
                || _limiter.IsBlocked(RateLimitBuckets.TokenFailIp, clientIpKey))
            {
                return false;
            }

            var now = Now();
            var candidate = sessionToken.Trim();

            // Read first: an unknown session must not cost a disk write.
            var known = await _store.ReadAsync(d => d.Tokens.Any(t => t.Purpose == TokenPurpose.ResetSession && t.UserId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash))).ConfigureAwait(false);
            if (!known)
            {
                RecordIpFailure(RateLimitBuckets.TokenFailIp, clientIpKey);
                return false;
            }

            Guid? userId = null;
            await _store.WriteAsync(d =>
            {
                d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
                var index = d.Tokens.FindIndex(t => t.Purpose == TokenPurpose.ResetSession && t.UserId is not null && _tokens.Matches(candidate, t.Hash));
                if (index >= 0)
                {
                    var owner = d.Tokens[index].UserId;
                    d.Tokens.RemoveAt(index);
                    if (owner is Guid c && IsEligible(d, c))
                    {
                        userId = c;
                    }
                }
            }).ConfigureAwait(false);

            if (userId is not Guid id || _users.GetUserById(id) is not { } user)
            {
                return false;
            }

            await _users.ChangePassword(id, newPassword).ConfigureAwait(false);
            await _store.WriteAsync(d => d.Tokens.RemoveAll(t => t.UserId == id && t.Purpose is TokenPurpose.ResetLink or TokenPurpose.ResetPin or TokenPurpose.ResetSession)).ConfigureAwait(false);
            _logger.LogInformation("Password of user {UserId} was reset by e-mail recovery", id);

            var cfg = _config();
            try
            {
                await _sessionCloser.CloseAsync(id, cfg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not close the sessions of user {UserId}", id);
            }

            var email = await _store.ReadAsync(d => d.Emails.FirstOrDefault(e => e.UserId == id)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(email?.Email))
            {
                var values = new Dictionary<string, string> { ["username"] = user.Username, ["serverName"] = ServerName() };
                var (subject, html, text) = EmailTemplates.Render("password-changed", LanguageOf(cfg), values, cfg.TemplateOverrides);
                _queue.Enqueue(new EmailMessage(email.Email, subject, html, text, "password-changed", id));
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password reset completion failed");
            return false;
        }
    }

    private static TokenRecord? FindPin(MailConciergeData d, Guid userId)
        => d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.ResetPin && t.UserId == userId);

    private static bool IsEligible(MailConciergeData d, Guid userId, IUserManager users, PluginConfiguration cfg)
        => users.GetUserById(userId) is { } user && AccountPolicy.IsEligible(user, d.Emails.FirstOrDefault(e => e.UserId == userId), cfg);

    private static string LanguageOf(PluginConfiguration cfg)
        => Languages.ForAccounts(cfg.DefaultLanguage);

    private bool IsEligible(MailConciergeData d, Guid userId) => IsEligible(d, userId, _users, _config());

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private string ServerName() => Languages.ServerName(_host);

    /// <summary>
    /// Checks the PIN and counts a failure in a single store write, so that parallel guesses cannot exceed the limit.
    /// </summary>
    private async Task<string?> Consume(Guid userId, Func<MailConciergeData, TokenRecord?> find, string pin, int maxFailures)
    {
        var now = Now();
        var sessionToken = _tokens.NewToken();
        string? session = null;

        // Read first: nothing to check (no active PIN, ineligible account) must not cost a disk write.
        var active = await _store.ReadAsync(d => find(d) is { } r && r.ExpiresAt > now && IsEligible(d, userId)).ConfigureAwait(false);
        if (!active)
        {
            return null;
        }

        await _store.WriteAsync(d =>
        {
            d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
            var record = find(d);
            if (record is null || !IsEligible(d, userId))
            {
                return;
            }

            if (_tokens.Matches(pin, record.Hash))
            {
                session = IssueSession(d, userId, sessionToken, now);
                return;
            }

            var index = d.Tokens.IndexOf(record);
            if (record.Failures + 1 >= maxFailures)
            {
                d.Tokens.RemoveAt(index);
            }
            else
            {
                d.Tokens[index] = record with { Failures = record.Failures + 1 };
            }
        }).ConfigureAwait(false);
        return session;
    }

    private string IssueSession(MailConciergeData d, Guid userId, string sessionToken, DateTime now)
    {
        d.Tokens.RemoveAll(t => t.UserId == userId && t.Purpose is TokenPurpose.ResetPin or TokenPurpose.ResetLink or TokenPurpose.ResetSession);
        d.Tokens.Add(new TokenRecord(_tokens.Hash(sessionToken), TokenPurpose.ResetSession, userId, null, null, now + SessionLifetime, 0));
        return sessionToken;
    }

    private void RecordIpFailure(string bucket, string clientIpKey)
    {
        if (!_limiter.TryAcquire(bucket, clientIpKey, FailuresBeforeBlock - 1, FailWindow))
        {
            _limiter.Penalize(bucket, clientIpKey, FailBlock);
        }
    }
}
