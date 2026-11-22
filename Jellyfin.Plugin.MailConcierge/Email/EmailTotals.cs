using System;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// Sent and failed e-mails.
/// </summary>
/// <param name="Sent">E-mails the server accepted.</param>
/// <param name="Failed">E-mails given up on.</param>
public sealed record EmailTotals(int Sent, int Failed);
