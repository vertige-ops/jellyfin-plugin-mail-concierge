using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// A rendered page and the nonce its inline elements use.
/// </summary>
public sealed class PageResult : ContentResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PageResult"/> class.
    /// </summary>
    /// <param name="nonce">The nonce of this response.</param>
    public PageResult(string nonce)
    {
        Nonce = nonce;
    }

    /// <summary>
    /// Gets the nonce of this response.
    /// </summary>
    public string Nonce { get; }
}
