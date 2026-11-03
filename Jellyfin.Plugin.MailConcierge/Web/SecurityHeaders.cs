using System;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Security headers of the public pages.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Sets the security headers on a response.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="nonce">The nonce allowed by the content security policy.</param>
    public static void Apply(HttpResponse response, string nonce)
    {
        ArgumentNullException.ThrowIfNull(response);
        var headers = response.Headers;
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'nonce-" + nonce
            + "'; style-src 'self' 'nonce-" + nonce
            + "'; img-src 'self' data:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Cache-Control"] = "no-store";
    }
}
