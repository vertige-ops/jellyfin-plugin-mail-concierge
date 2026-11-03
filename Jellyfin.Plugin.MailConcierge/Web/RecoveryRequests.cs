namespace Jellyfin.Plugin.MailConcierge.Web;

#pragma warning disable SA1402, SA1649 // Small related request types kept together.

/// <summary>
/// The body of <c>POST /MailConcierge/Reset/Link</c>.
/// </summary>
/// <param name="Token">The token of the e-mailed link.</param>
public sealed record LinkRequest(string? Token);

/// <summary>
/// The body of <c>POST /MailConcierge/Cancel</c>.
/// </summary>
/// <param name="Token">The token of the e-mailed link.</param>
/// <param name="Password">The current password of the account.</param>
public sealed record CancelRequest(string? Token, string? Password);

/// <summary>
/// The body of <c>POST /MailConcierge/Reset/Pin</c>.
/// </summary>
/// <param name="Username">The user name.</param>
/// <param name="Pin">The e-mailed PIN.</param>
public sealed record PinRequest(string? Username, string? Pin);

/// <summary>
/// The body of <c>POST /MailConcierge/Reset/Complete</c>.
/// </summary>
/// <param name="Session">The reset session.</param>
/// <param name="Password">The new password.</param>
public sealed record CompleteRequest(string? Session, string? Password);
