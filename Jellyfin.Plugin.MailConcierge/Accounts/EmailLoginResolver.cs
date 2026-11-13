using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>
/// Decides when an e-mail address typed at sign-in stands for the user name of an account. Rules 2 to 6 of the e-mail sign-in:
/// the user name always wins, the address must be the confirmed address of exactly one eligible account, and Jellyfin's own
/// failure counter of that account must allow one more attempt. Nothing is counted or stored here: the counter of Jellyfin is
/// read on every call, and no password is ever seen.
/// </summary>
public sealed class EmailLoginResolver
{
    /// <summary>
    /// The failures recorded by Jellyfin on an account from which a typed address is no longer translated to its user name.
    /// </summary>
    public const int MaxFailuresByEmail = 3;

    private readonly IUserManager _users;
    private readonly EmailService _email;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailLoginResolver"/> class.
    /// </summary>
    /// <param name="users">The user manager.</param>
    /// <param name="email">The e-mail service (stored addresses).</param>
    /// <param name="config">Accessor of the current configuration.</param>
    public EmailLoginResolver(IUserManager users, EmailService email, Func<PluginConfiguration> config)
    {
        _users = users;
        _email = email;
        _config = config;
    }

    /// <summary>
    /// Finds the account whose confirmed address was typed in place of a user name. Errors of the user manager or of the
    /// store are not caught.
    /// </summary>
    /// <param name="entered">The text typed as the user name.</param>
    /// <returns>
    /// The account id; null unless the text contains <c>@</c>, no account bears that name, as typed or without its surrounding
    /// spaces (ignoring case, as Jellyfin does), and the normalized address is the confirmed address of exactly one account.
    /// </returns>
    public async Task<Guid?> FindAccountAsync(string? entered)
    {
        if (entered is null || !entered.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        // The user name always wins, on the raw text Jellyfin gets at sign-in and on the trimmed text the address is read from.
        var trimmed = entered.Trim();
        if (_users.GetUserByName(entered) is not null
            || (!string.Equals(trimmed, entered, StringComparison.Ordinal) && _users.GetUserByName(trimmed) is not null)
            || !EmailService.TryNormalize(entered, out var address))
        {
            return null;
        }

        var owners = (await _email.ListAsync().ConfigureAwait(false))
            .Where(e => e.ConfirmedAt is not null && string.Equals(e.Email, address, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.UserId)
            .Distinct()
            .Take(2)
            .ToList();
        return owners.Count == 1 ? owners[0] : null;
    }

    /// <summary>
    /// Serializes the translated sign-in requests of one account, so that simultaneous attempts cannot overshoot the failure
    /// limits. Requests for other accounts never wait. This is the same lock as the password checks of the Mail Concierge forms
    /// (<see cref="EmailService.LockAccountAsync"/>).
    /// </summary>
    /// <param name="userId">The account id.</param>
    /// <param name="cancellationToken">Ends the wait for the account; the account is then not taken.</param>
    /// <returns>A handle that releases the account when disposed.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled before the account was free.</exception>
    public Task<IDisposable> LockAccountAsync(Guid userId, CancellationToken cancellationToken = default)
        => _email.LockAccountAsync(userId, cancellationToken);

    /// <summary>
    /// Checks, on a fresh read of the account, that its typed address may be translated: the account is eligible
    /// (<see cref="AccountPolicy.IsEligible"/>), has a stored password, and <see cref="AllowsAttempt"/> holds for its current
    /// failure counter. Only the stored hash is tested for emptiness; no typed password is read.
    /// Errors of the user manager or of the store are not caught.
    /// </summary>
    /// <param name="userId">The account id.</param>
    /// <returns>
    /// The user name to substitute, or null when the account is gone, not eligible, without a password or too close to a lockout.
    /// </returns>
    public async Task<string?> UsernameIfAllowedAsync(Guid userId)
    {
        if (_users.GetUserById(userId) is not { } user)
        {
            return null;
        }

        var email = await _email.GetAsync(userId).ConfigureAwait(false);
        // An account without a password is not offered by address: an address is far more widely known than a user name.
        if (!AccountPolicy.IsEligible(user, email, _config())
            || string.IsNullOrEmpty(user.Password)
            || !AllowsAttempt(user.InvalidLoginAttemptCount, user.LoginAttemptsBeforeLockout))
        {
            return null;
        }

        return user.Username;
    }

    /// <summary>
    /// Tells whether one more sign-in attempt by address is allowed: fewer than <see cref="MaxFailuresByEmail"/> failures, and
    /// one more failure would not reach the lockout threshold of the account.
    /// </summary>
    /// <param name="invalidLoginAttemptCount">The failure counter kept by Jellyfin for the account.</param>
    /// <param name="loginAttemptsBeforeLockout">The lockout threshold of the account, or null when it has none.</param>
    /// <returns>True when the attempt may be translated.</returns>
    public static bool AllowsAttempt(int invalidLoginAttemptCount, int? loginAttemptsBeforeLockout)
        => invalidLoginAttemptCount < MaxFailuresByEmail
            && !AccountPolicy.NextFailureLocksOut(invalidLoginAttemptCount, loginAttemptsBeforeLockout);
}
