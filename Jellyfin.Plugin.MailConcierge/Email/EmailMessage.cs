using System;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// An e-mail ready to be sent.
/// </summary>
/// <param name="To">The recipient address (never logged).</param>
/// <param name="Subject">The subject.</param>
/// <param name="Html">The HTML body (never logged).</param>
/// <param name="Text">The plain text body (never logged).</param>
/// <param name="Template">The template name, for logging only.</param>
/// <param name="UserId">The Jellyfin user concerned, for logging only.</param>
public sealed record EmailMessage(string To, string Subject, string Html, string Text, string Template, Guid? UserId);
