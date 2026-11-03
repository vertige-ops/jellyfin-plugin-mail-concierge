namespace Jellyfin.Plugin.MailConcierge.Web;

#pragma warning disable SA1402, SA1649 // Small related request types kept together.
#pragma warning disable CA1819 // LibraryIds is a JSON array.

/// <summary>
/// The body of <c>POST /MailConcierge/Accept/Check</c>.
/// </summary>
/// <param name="Token">The token of the invitation link.</param>
/// <param name="Username">The user name to check.</param>
public sealed record AcceptCheckRequest(string? Token, string? Username);

/// <summary>
/// The body of <c>POST /MailConcierge/Accept</c>.
/// </summary>
/// <param name="Token">The token of the invitation link.</param>
/// <param name="Username">The chosen user name.</param>
/// <param name="Email">The chosen address.</param>
/// <param name="Password">The chosen password.</param>
/// <param name="Language">The language of the e-mails (fr or en).</param>
/// <param name="Avatar">The optional profile picture, base64 without data-URL prefix.</param>
public sealed record AcceptRequest(string? Token, string? Username, string? Email, string? Password, string? Language, string? Avatar = null);

/// <summary>
/// The body of <c>POST /MailConcierge/Invitations</c>.
/// </summary>
/// <param name="ProposedUsername">The proposed user name.</param>
/// <param name="Email">The invited address, or empty for a link to copy.</param>
/// <param name="LibraryIds">The libraries to grant.</param>
/// <param name="Days">The validity in days, or null for the default.</param>
/// <param name="Language">The invitation language (fr or en).</param>
public sealed record CreateInvitationRequest(string? ProposedUsername, string? Email, string[]? LibraryIds, int? Days, string? Language);
