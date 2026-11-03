# Roadmap

This is a plan, not a promise: Mail Concierge is maintained by one person, and dates are intentions. E-mail is the only channel and no other is planned.

## 12.0.x: stability only

Bug and security fixes. No new features, so that 12.0 stays as stable as possible. Security problems are fixed first and disclosed after the fix is released (see [SECURITY.md](../SECURITY.md)). The project overview is in the [README](../README.md).

## 12.1: setup and reliability (planned soon after 12.0)

- Refuse to enable the plugin until the SMTP settings are valid.
- SMTP presets (for example Google, Proton, Custom) that fill in host, port and security, with a link to the provider's official documentation.
- Reject a URL typed in the SMTP host field.
- Delete invitations from the list.
- Show the e-mail budget counters in the administration page.
- A persistent e-mail queue with spaced retries and a visible send status per invitation.
- Sign in with the user name or the confirmed e-mail address.
- Portuguese (Brazil) and Simplified Chinese.
- Move the reset token out of the URL.
- Invitation list: a status badge per invitation and a confirmation before revoking.
- A setting to turn off the logo in e-mails, or to embed it in the message instead of loading it from your server.
- Profile: a single current-password field, and a translated picture button with a preview.
- Public pages: show which account the page is about.
- Refreshed screenshots for every page and e-mail.

## 12.2 and later: depth (in six months to a year, if needed)

- Access profiles (predefined library sets) for invitations.
- Links to the documentation from the tooltips.
- An end-to-end test against a real jellyfin-web.
- Accessibility review.
- A schema version for the stored data.
- Re-encoding of uploaded avatars.
- Follow new Jellyfin releases (see the compatibility table in the README).

## Versioning

`<Jellyfin major>.<release>.<fix>.0`: the first number is the major Jellyfin version the release is built for (12 for Jellyfin 12), the second grows with each feature release, the third with each fix. The exact Jellyfin version required is the `targetAbi` in `manifest.json`. A new Jellyfin major version starts a new series (13.0.0.0); older releases stay available for older servers.
