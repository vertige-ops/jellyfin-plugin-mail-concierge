using System;

namespace Jellyfin.Plugin.MailConcierge.Data;

#pragma warning disable SA1402, SA1649 // Small related types kept together.
#pragma warning disable CA1819 // LibraryIds is a positional record array serialized as JSON.

/// <summary>State of an invitation.</summary>
public enum InvitationState
{
    /// <summary>Waiting to be accepted.</summary>
    Pending,

    /// <summary>Accepted, the account exists.</summary>
    Accepted,

    /// <summary>Revoked by an administrator.</summary>
    Revoked,

    /// <summary>Past its expiry date.</summary>
    Expired
}

/// <summary>Purpose of a stored token.</summary>
public enum TokenPurpose
{
    /// <summary>Invitation link.</summary>
    Invitation,

    /// <summary>Password reset link.</summary>
    ResetLink,

    /// <summary>Password reset PIN.</summary>
    ResetPin,

    /// <summary>E-mail address confirmation link.</summary>
    ConfirmEmail,

    /// <summary>Link cancelling an e-mail change.</summary>
    CancelEmailChange,

    /// <summary>Short-lived reset session.</summary>
    ResetSession
}

/// <summary>E-mail settings of one user.</summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="Email">The confirmed address.</param>
/// <param name="PendingEmail">The address awaiting confirmation.</param>
/// <param name="Language">A language code kept for older data files; e-mails use the configured default language and never read it.</param>
/// <param name="ConfirmedAt">When <paramref name="Email"/> was confirmed (UTC).</param>
public sealed record UserEmail(Guid UserId, string? Email, string? PendingEmail, string Language, DateTime? ConfirmedAt);

/// <summary>An invitation to create an account.</summary>
/// <param name="Id">The invitation id.</param>
/// <param name="ProposedUsername">The proposed user name.</param>
/// <param name="Email">The invitee address.</param>
/// <param name="LibraryIds">The libraries granted to the new account.</param>
/// <param name="Language">The invitation language code.</param>
/// <param name="ExpiresAt">Expiry date (UTC).</param>
/// <param name="State">Current state.</param>
/// <param name="CreatedAt">Creation date (UTC).</param>
public sealed record Invitation(
    Guid Id,
    string ProposedUsername,
    string? Email,
    string[] LibraryIds,
    string Language,
    DateTime ExpiresAt,
    InvitationState State,
    DateTime CreatedAt);

/// <summary>A hashed token or PIN. The clear value is never stored.</summary>
/// <param name="Hash">The HMAC-SHA256 hash, base64url.</param>
/// <param name="Purpose">What the token is for.</param>
/// <param name="UserId">The related user, if any.</param>
/// <param name="InvitationId">The related invitation, if any.</param>
/// <param name="Payload">Optional purpose-specific data.</param>
/// <param name="ExpiresAt">Expiry date (UTC).</param>
/// <param name="Failures">Number of failed attempts.</param>
public sealed record TokenRecord(
    string Hash,
    TokenPurpose Purpose,
    Guid? UserId,
    Guid? InvitationId,
    string? Payload,
    DateTime ExpiresAt,
    int Failures);
