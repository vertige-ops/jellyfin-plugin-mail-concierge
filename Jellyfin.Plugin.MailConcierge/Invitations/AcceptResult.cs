namespace Jellyfin.Plugin.MailConcierge.Invitations;

/// <summary>The outcome of accepting an invitation.</summary>
public enum AcceptResult
{
    /// <summary>The account was created.</summary>
    Ok,

    /// <summary>The link is unknown, expired, revoked or already used.</summary>
    InvalidToken,

    /// <summary>A user with this name already exists.</summary>
    UsernameTaken,

    /// <summary>The user name has characters Jellyfin does not accept.</summary>
    InvalidUsername,

    /// <summary>The e-mail address is not valid.</summary>
    InvalidEmail,

    /// <summary>The password is too short.</summary>
    WeakPassword
}
