using System;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// The e-mail counters of one account.
/// </summary>
/// <param name="UserId">The account id.</param>
/// <param name="Sent">E-mails the server accepted.</param>
/// <param name="Failed">E-mails given up on.</param>
/// <param name="LastSentAt">When the last e-mail was accepted, or null when none was.</param>
public sealed record AccountEmailStats(Guid UserId, int Sent, int Failed, DateTimeOffset? LastSentAt);
