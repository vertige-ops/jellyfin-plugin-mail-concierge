using System;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// Seerr cannot be used for an administrator action: the integration is disabled or Seerr cannot be reached.
/// </summary>
public sealed class SeerrUnavailableException : Exception
{
    /// <summary>The code returned when the integration is disabled.</summary>
    public const string Disabled = "seerr_disabled";

    /// <summary>The code returned when Seerr is unreachable, refuses the key or answers with an error.</summary>
    public const string Unreachable = "seerr_unreachable";

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrUnavailableException"/> class.
    /// </summary>
    public SeerrUnavailableException()
        : this(Unreachable)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrUnavailableException"/> class.
    /// </summary>
    /// <param name="code">The code, <see cref="Disabled"/> or <see cref="Unreachable"/>.</param>
    public SeerrUnavailableException(string code)
        : base(code)
    {
        Code = code;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrUnavailableException"/> class.
    /// </summary>
    /// <param name="code">The code, <see cref="Disabled"/> or <see cref="Unreachable"/>.</param>
    /// <param name="innerException">The cause.</param>
    public SeerrUnavailableException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the machine-readable code.
    /// </summary>
    public string Code { get; }
}
