namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// The outcome of <see cref="EmailQueue.Enqueue"/>.
/// </summary>
public enum EnqueueResult
{
    /// <summary>The message was queued for sending.</summary>
    Queued,

    /// <summary>The message was dropped: e-mail sending is not configured.</summary>
    NotConfigured,

    /// <summary>The message was dropped: the hourly budget of its type is exhausted.</summary>
    LimitReached,
}
