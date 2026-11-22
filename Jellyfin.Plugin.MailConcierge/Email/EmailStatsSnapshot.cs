using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// A copy of the e-mail counters.
/// </summary>
/// <param name="Since">When the counting started (the plugin start).</param>
/// <param name="Invitations">The invitation totals.</param>
/// <param name="Users">The per-account counters.</param>
public sealed record EmailStatsSnapshot(DateTimeOffset Since, EmailTotals Invitations, IReadOnlyList<AccountEmailStats> Users);
