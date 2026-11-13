using System;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// Thrown by an administrative operation that would create or send something while the <see cref="ServiceGate"/> is closed.
/// </summary>
/// <remarks>
/// Not an <see cref="InvalidOperationException"/>: the controllers already map that one to <c>no_public_url</c>.
/// </remarks>
public sealed class ServiceClosedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceClosedException"/> class.
    /// </summary>
    public ServiceClosedException()
        : base("Mail Concierge is disabled or blocked by the proxy guard.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceClosedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ServiceClosedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceClosedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ServiceClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
