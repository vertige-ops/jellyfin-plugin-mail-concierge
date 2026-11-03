namespace Jellyfin.Plugin.MailConcierge.Invitations;

/// <summary>What the acceptance page shows about an invitation.</summary>
/// <param name="ProposedUsername">The proposed user name.</param>
/// <param name="Email">The invited address, if any.</param>
/// <param name="Language">The invitation language code.</param>
public sealed record InvitationView(string ProposedUsername, string? Email, string Language);
