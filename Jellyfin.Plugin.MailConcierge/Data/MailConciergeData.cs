using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MailConcierge.Data;

#pragma warning disable CA1002 // List<T> is the mutation surface of the store.
/// <summary>The content of the JSON store.</summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class MailConciergeData
{
    /// <summary>Gets the user e-mail settings.</summary>
    public List<UserEmail> Emails { get; } = [];

    /// <summary>Gets the invitations.</summary>
    public List<Invitation> Invitations { get; } = [];

    /// <summary>Gets the hashed tokens.</summary>
    public List<TokenRecord> Tokens { get; } = [];

    /// <summary>Gets the users whose confirmed e-mail must still be pushed to Seerr.</summary>
    public List<Guid> PendingSeerr { get; } = [];
}
#pragma warning restore CA1002
