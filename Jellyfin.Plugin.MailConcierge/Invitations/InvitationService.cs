using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using Jellyfin.Plugin.MailConcierge.Security;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

#pragma warning disable SA1316 // The tuple element names are part of the contract.

namespace Jellyfin.Plugin.MailConcierge.Invitations;

/// <summary>
/// Invitations: an administrator invites someone by e-mail or by a copied link, the invitee chooses a user name, an address
/// and a password, and the account is created only then, with the invited libraries and never as an administrator.
/// </summary>
/// <remarks>
/// Only the hash of the link token is stored, so the link is returned when it is created or regenerated and never again.
/// </remarks>
public sealed class InvitationService
{
    /// <summary>
    /// The number of user name checks a link allows per ten minutes.
    /// </summary>
    public const int MaxUsernameChecks = 20;

    private const int DefaultLoginAttemptsBeforeLockout = 3;

    private const int MaxDays = 365;
    private const int FailuresBeforeBlock = 10;

    private static readonly TimeSpan RetentionAfterEnd = TimeSpan.FromDays(30);
    private static readonly TimeSpan CheckWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailBlock = TimeSpan.FromMinutes(15);

    // The rule of Jellyfin's UserManager (its check is internal): unicode word characters, space, - ' . _ @ +, no edge space.
    private static readonly Regex ValidUsername = new(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly RateLimiter _limiter;
    private readonly EmailQueue _queue;
    private readonly IUserManager _users;
    private readonly EmailService _email;
    private readonly IServerApplicationHost _host;
    private readonly Func<PluginConfiguration> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<InvitationService> _logger;
    private readonly AvatarSaver? _avatars;

    /// <summary>
    /// Initializes a new instance of the <see cref="InvitationService"/> class.
    /// </summary>
    /// <param name="store">The data store.</param>
    /// <param name="tokens">The token service.</param>
    /// <param name="limiter">The rate limiter.</param>
    /// <param name="queue">The e-mail queue.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="email">The e-mail service.</param>
    /// <param name="host">The application host (server name).</param>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="time">The time source.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="avatars">Saves the optional profile picture.</param>
    public InvitationService(
        MailConciergeStore store,
        TokenService tokens,
        RateLimiter limiter,
        EmailQueue queue,
        IUserManager users,
        EmailService email,
        IServerApplicationHost host,
        Func<PluginConfiguration> config,
        TimeProvider time,
        ILogger<InvitationService> logger,
        AvatarSaver? avatars = null)
    {
        _store = store;
        _tokens = tokens;
        _limiter = limiter;
        _queue = queue;
        _users = users;
        _email = email;
        _host = host;
        _config = config;
        _time = time;
        _logger = logger;
        _avatars = avatars;
    }

    /// <summary>
    /// Gets whether a user name is accepted by Jellyfin.
    /// </summary>
    /// <param name="username">The candidate name.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidUsername(string? username)
        => !string.IsNullOrWhiteSpace(username) && ValidUsername.IsMatch(username) && username is not ("." or "..");

    /// <summary>
    /// Creates an invitation and sends it when an address is given and e-mail is configured.
    /// </summary>
    /// <param name="proposedUsername">The proposed user name.</param>
    /// <param name="email">The invited address, or null for a link to copy.</param>
    /// <param name="libraryIds">The libraries to grant (library ids).</param>
    /// <param name="days">The validity in days, or null (or zero or less) for the configured default.</param>
    /// <param name="language">The invitation language; anything but fr and en means the default language.</param>
    /// <returns>The invitation, the link (which is not stored), and what happened to the e-mail, or null when none was to be sent.</returns>
    /// <exception cref="ArgumentException">The user name, the address or a library id is not valid.</exception>
    /// <exception cref="InvalidOperationException">The public URL is not configured, so no usable link can be made.</exception>
    public async Task<(Invitation inv, string link, EnqueueResult? email)> CreateAsync(string proposedUsername, string? email, string[] libraryIds, int? days, string language)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        var username = proposedUsername?.Trim();
        if (!IsValidUsername(username))
        {
            throw new ArgumentException("The user name is not valid.", nameof(proposedUsername));
        }

        string? address = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            if (!EmailService.TryNormalize(email, out var normalized))
            {
                throw new ArgumentException("The e-mail address is not valid.", nameof(email));
            }

            address = normalized;
        }

        var libraries = libraryIds.Select(l => Guid.TryParse(l, out var g) ? g : throw new ArgumentException("A library id is not valid.", nameof(libraryIds)))
            .Distinct()
            .Select(g => g.ToString("N"))
            .ToArray();
        var cfg = _config();
        RequirePublicUrl(cfg);
        var now = Now();
        var lang = Languages.Normalize(language, cfg.DefaultLanguage);
        var inv = new Invitation(Guid.NewGuid(), username!, address, libraries, lang, now.AddDays(Math.Clamp(days is > 0 ? days.Value : cfg.InvitationDays, 1, MaxDays)), InvitationState.Pending, now);
        var token = _tokens.NewToken();
        await _store.WriteAsync(d =>
        {
            d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
            d.Invitations.Add(inv);
            d.Tokens.Add(new TokenRecord(_tokens.Hash(token), TokenPurpose.Invitation, null, inv.Id, null, inv.ExpiresAt, 0));
        }).ConfigureAwait(false);

        var link = LinkOf(cfg, token);
        EnqueueResult? emailResult = null;
        if (address is not null)
        {
            var values = new Dictionary<string, string>
            {
                ["username"] = inv.ProposedUsername,
                ["serverName"] = ServerName(),
                ["link"] = link,
                ["expires"] = FormatDate(inv.ExpiresAt, lang),
            };
            var (subject, html, text) = EmailTemplates.Render("invitation", lang, values, cfg.TemplateOverrides);
            emailResult = _queue.Enqueue(new EmailMessage(address, subject, html, text, "invitation", null));
        }

        return (inv, link, emailResult);
    }

    /// <summary>
    /// Lists the invitations, marking the pending ones that expired as expired. Finished invitations (accepted, revoked, expired) are dropped 30 days after their expiry date.
    /// </summary>
    /// <returns>Copies of the invitations, newest first. They carry no token.</returns>
    public async Task<IReadOnlyList<Invitation>> ListAsync()
    {
        var now = Now();
        IReadOnlyList<Invitation> result = Array.Empty<Invitation>();
        await _store.WriteAsync(d =>
        {
            d.Invitations.RemoveAll(i => i.State != InvitationState.Pending && i.ExpiresAt <= now - RetentionAfterEnd);
            for (var i = 0; i < d.Invitations.Count; i++)
            {
                if (d.Invitations[i].State == InvitationState.Pending && d.Invitations[i].ExpiresAt <= now)
                {
                    d.Invitations[i] = d.Invitations[i] with { State = InvitationState.Expired };
                }
            }

            result = d.Invitations.OrderByDescending(i => i.CreatedAt).ToList();
        }).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Revokes a pending or expired invitation: its link stops working. Unknown or accepted invitations are left alone.
    /// </summary>
    /// <param name="id">The invitation id.</param>
    /// <returns>A task that completes when the invitation was revoked.</returns>
    public Task RevokeAsync(Guid id) => _store.WriteAsync(d =>
    {
        var index = d.Invitations.FindIndex(i => i.Id == id);
        if (index >= 0 && d.Invitations[index].State is InvitationState.Pending or InvitationState.Expired)
        {
            d.Invitations[index] = d.Invitations[index] with { State = InvitationState.Revoked };
            d.Tokens.RemoveAll(t => t.Purpose == TokenPurpose.Invitation && t.InvitationId == id);
        }
    });

    /// <summary>
    /// Gives a pending or expired invitation a new link: the old link stops working and the validity restarts.
    /// </summary>
    /// <param name="id">The invitation id.</param>
    /// <returns>The new link, which is not stored.</returns>
    /// <exception cref="KeyNotFoundException">There is no such invitation that can still be used.</exception>
    /// <exception cref="InvalidOperationException">The public URL is not configured.</exception>
    public async Task<string> RegenerateAsync(Guid id)
    {
        var cfg = _config();
        RequirePublicUrl(cfg);
        var now = Now();
        var token = _tokens.NewToken();
        var found = false;
        await _store.WriteAsync(d =>
        {
            d.Tokens.RemoveAll(t => t.ExpiresAt <= now || (t.Purpose == TokenPurpose.Invitation && t.InvitationId == id));
            var index = d.Invitations.FindIndex(i => i.Id == id);
            if (index < 0 || d.Invitations[index].State is not (InvitationState.Pending or InvitationState.Expired))
            {
                return;
            }

            var current = d.Invitations[index];
            var span = current.ExpiresAt - current.CreatedAt;
            var expires = now + (span < TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : span);
            d.Invitations[index] = current with { State = InvitationState.Pending, ExpiresAt = expires };
            d.Tokens.Add(new TokenRecord(_tokens.Hash(token), TokenPurpose.Invitation, null, id, null, expires, 0));
            found = true;
        }).ConfigureAwait(false);
        return found ? LinkOf(cfg, token) : throw new KeyNotFoundException("No usable invitation has this id.");
    }

    /// <summary>
    /// Reads what the acceptance page shows, without consuming the link.
    /// </summary>
    /// <param name="token">The token of the link.</param>
    /// <param name="clientIpKey">The client IP key, fed to the token-failure limiter.</param>
    /// <returns>The proposed user name and address, or null when the link is not valid.</returns>
    public async Task<InvitationView?> PeekAsync(string token, string clientIpKey = "unknown")
    {
        var inv = await FindAsync(token, clientIpKey).ConfigureAwait(false);
        return inv is null ? null : new InvitationView(inv.ProposedUsername, inv.Email, inv.Language);
    }

    /// <summary>
    /// Tells whether a user name can be chosen. The link must be valid and is limited to <see cref="MaxUsernameChecks"/> checks per ten minutes.
    /// </summary>
    /// <param name="token">The token of the link.</param>
    /// <param name="username">The candidate name.</param>
    /// <param name="clientIpKey">The client IP key, fed to the token-failure limiter.</param>
    /// <returns>True when the link is valid, not limited, and the name is valid and free.</returns>
    public async Task<bool> IsUsernameAvailableAsync(string token, string username, string clientIpKey = "unknown")
    {
        if (await FindAsync(token, clientIpKey).ConfigureAwait(false) is null)
        {
            return false;
        }

        if (!_limiter.TryAcquire(RateLimitBuckets.UsernameCheckToken, _tokens.Hash(token.Trim()), MaxUsernameChecks, CheckWindow))
        {
            return false;
        }

        var name = username?.Trim();
        return IsValidUsername(name) && _users.GetUserByName(name!) is null;
    }

    /// <summary>
    /// Accepts an invitation: validates the choices, creates the account with the invited libraries, sets the password
    /// and the address, and ends the invitation. Call it from the POST handler only.
    /// </summary>
    /// <param name="token">The token of the link.</param>
    /// <param name="username">The chosen user name.</param>
    /// <param name="email">The chosen address. It is confirmed when it is the invited one, otherwise a confirmation is sent.</param>
    /// <param name="password">The chosen password.</param>
    /// <param name="language">The language of the e-mails; anything but fr and en means the invitation language.</param>
    /// <param name="clientIpKey">The client IP key, fed to the token-failure limiter.</param>
    /// <param name="avatarBase64">The optional profile picture, base64 without prefix; invalid means none; it is applied last and its failure never fails the acceptance.</param>
    /// <returns>The outcome. Nothing is consumed unless it is <see cref="AcceptResult.Ok"/>, or an exception is thrown after the changes were undone.</returns>
    public async Task<AcceptResult> AcceptAsync(string token, string username, string email, string password, string language, string clientIpKey = "unknown", string? avatarBase64 = null)
    {
        if (await FindAsync(token, clientIpKey).ConfigureAwait(false) is not { } inv)
        {
            return AcceptResult.InvalidToken;
        }

        var name = username?.Trim();
        if (!IsValidUsername(name))
        {
            return AcceptResult.InvalidUsername;
        }

        // The existence test shares the budget of the availability check, so posting candidate names cannot bypass it.
        if (!_limiter.TryAcquire(RateLimitBuckets.UsernameCheckToken, _tokens.Hash(token.Trim()), MaxUsernameChecks, CheckWindow)
            || _users.GetUserByName(name!) is not null)
        {
            return AcceptResult.UsernameTaken;
        }

        if (!EmailService.TryNormalize(email, out var address))
        {
            return AcceptResult.InvalidEmail;
        }

        if (!RecoveryService.IsAcceptablePassword(password))
        {
            return AcceptResult.WeakPassword;
        }

        // One winner: whoever removes the link from the store creates the account.
        var claimed = await ClaimAsync(token.Trim(), inv.Id).ConfigureAwait(false);
        if (claimed is null)
        {
            RecordIpFailure(clientIpKey);
            return AcceptResult.InvalidToken;
        }

        User? user = null;
        try
        {
            try
            {
                user = await _users.CreateUserAsync(name!).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // Created by someone else since the check above.
                await RestoreAsync(claimed).ConfigureAwait(false);
                return AcceptResult.UsernameTaken;
            }

            var policy = _users.GetUserDto(user)?.Policy ?? new UserPolicy();
            policy.IsAdministrator = false;

            // Disabled until the password is set: a new account has an empty password, which must never let anyone sign in.
            policy.IsDisabled = true;
            policy.LoginAttemptsBeforeLockout = DefaultLoginAttemptsBeforeLockout;
            policy.EnableAllFolders = false;
            policy.EnabledFolders = inv.LibraryIds.Select(Guid.Parse).ToArray();
            policy.PasswordResetProviderId = MailConciergePasswordResetProvider.Id;
            await _users.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            await _users.ChangePassword(user.Id, password).ConfigureAwait(false);
            policy.IsDisabled = false;
            await _users.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);

            var lang = string.IsNullOrWhiteSpace(language) ? inv.Language : language;
            if (string.Equals(address, inv.Email, StringComparison.OrdinalIgnoreCase))
            {
                await _email.SetByAdminAsync(user.Id, address).ConfigureAwait(false);
            }
            else
            {
                await _email.SetPendingForNewAccountAsync(user, address, lang).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (user is not null || ex is not ArgumentException)
        {
            _logger.LogError(ex, "Accepting invitation {InvitationId} failed, undoing it", inv.Id);
            if (user is not null)
            {
                await DeleteQuietly(user.Id).ConfigureAwait(false);
            }

            await RestoreAsync(claimed).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation("Invitation {InvitationId} accepted, user {UserId} created", inv.Id, user.Id);
        await ApplyAvatarAsync(user.Id, avatarBase64).ConfigureAwait(false);
        return AcceptResult.Ok;
    }

    private async Task ApplyAvatarAsync(Guid userId, string? avatarBase64)
    {
        if (_avatars is null || string.IsNullOrEmpty(avatarBase64) || !AvatarImage.TryDecode(avatarBase64, out var bytes, out var mimeType))
        {
            return;
        }

        try
        {
            if (!await _avatars.SaveAsync(userId, bytes, mimeType).ConfigureAwait(false))
            {
                _logger.LogWarning("The profile picture of user {UserId} could not be saved", userId);
            }
        }
        catch (Exception ex)
        {
            // Never the content, never the token: the registration succeeded anyway.
            _logger.LogWarning("The profile picture of user {UserId} could not be saved ({ErrorType})", userId, ex.GetType().Name);
        }
    }

    private static void RequirePublicUrl(PluginConfiguration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.PublicUrl))
        {
            throw new InvalidOperationException("The public URL is not configured.");
        }
    }

    private static string LinkOf(PluginConfiguration cfg, string token) => cfg.PublicUrl.TrimEnd('/') + "/MailConcierge/Accept?t=" + token;

    private static bool IsUsable(Invitation inv, DateTime now) => inv.State == InvitationState.Pending && inv.ExpiresAt > now;

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private string ServerName() => Languages.ServerName(_host);

    private string FormatDate(DateTime utc, string language)
        => Languages.FormatDate(utc, _time.LocalTimeZone, language);

    /// <summary>
    /// Finds the usable invitation of a link without changing anything; counts a failure for the client otherwise.
    /// </summary>
    private async Task<Invitation?> FindAsync(string token, string clientIpKey)
    {
        if (_limiter.IsBlocked(RateLimitBuckets.TokenFailIp, clientIpKey))
        {
            return null;
        }

        Invitation? found = null;
        if (!string.IsNullOrWhiteSpace(token))
        {
            var candidate = token.Trim();
            var now = Now();
            found = await _store.ReadAsync(d =>
            {
                var record = d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.Invitation && t.InvitationId is not null && t.ExpiresAt > now && _tokens.Matches(candidate, t.Hash));
                return d.Invitations.FirstOrDefault(i => i.Id == record?.InvitationId && IsUsable(i, now));
            }).ConfigureAwait(false);
        }

        if (found is null)
        {
            RecordIpFailure(clientIpKey);
        }

        return found;
    }

    private async Task<Claim?> ClaimAsync(string candidate, Guid invitationId)
    {
        var now = Now();
        Claim? claimed = null;
        await _store.WriteAsync(d =>
        {
            d.Tokens.RemoveAll(t => t.ExpiresAt <= now);
            var record = d.Tokens.FirstOrDefault(t => t.Purpose == TokenPurpose.Invitation && t.InvitationId == invitationId && _tokens.Matches(candidate, t.Hash));
            var index = d.Invitations.FindIndex(i => i.Id == invitationId);
            if (record is null || index < 0 || !IsUsable(d.Invitations[index], now))
            {
                return;
            }

            claimed = new Claim(record, d.Invitations[index]);
            d.Tokens.Remove(record);
            d.Invitations[index] = d.Invitations[index] with { State = InvitationState.Accepted };
        }).ConfigureAwait(false);
        return claimed;
    }

    private async Task RestoreAsync(Claim claimed)
    {
        try
        {
            await _store.WriteAsync(d =>
            {
                var index = d.Invitations.FindIndex(i => i.Id == claimed.Invitation.Id);
                if (index >= 0 && d.Invitations[index].State == InvitationState.Accepted)
                {
                    d.Invitations[index] = claimed.Invitation;
                    d.Tokens.Add(claimed.Token);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not restore invitation {InvitationId}", claimed.Invitation.Id);
        }
    }

    private async Task DeleteQuietly(Guid userId)
    {
        try
        {
            await _users.DeleteUserAsync(userId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not delete the account {UserId} left by a failed invitation", userId);
        }
    }

    private void RecordIpFailure(string clientIpKey)
    {
        if (!_limiter.TryAcquire(RateLimitBuckets.TokenFailIp, clientIpKey, FailuresBeforeBlock - 1, FailWindow))
        {
            _limiter.Penalize(RateLimitBuckets.TokenFailIp, clientIpKey, FailBlock);
        }
    }

    private sealed record Claim(TokenRecord Token, Invitation Invitation);
}
