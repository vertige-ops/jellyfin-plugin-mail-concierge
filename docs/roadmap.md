# Roadmap

This is a plan, not a promise: Mail Concierge is maintained by one person, and dates are intentions. E-mail is the only channel and no other is planned.

## 12.0.0.x: stability only

Bug and security fixes only, for Jellyfin 12.1 to 12.3. Security problems are fixed first and disclosed after the fix is released (see [SECURITY.md](../SECURITY.md)). The project overview is in the [README](../README.md).

## Delivered in 12.4.0.0

- Sign in with the confirmed e-mail address (off by default). See the [changelog](../CHANGELOG.md).

## 12.4.1.0: setup and reliability (next feature release)

- Refuse to enable the plugin until the SMTP settings are valid.
- SMTP presets (for example Google, Proton, Custom) that fill in host, port and security, with a link to the provider's official documentation.
- Reject a URL typed in the SMTP host field.
- Delete invitations from the list.
- Show the e-mail budget counters in the administration page.
- A persistent e-mail queue with spaced retries and a visible send status per invitation.
- Portuguese (Brazil) and Simplified Chinese.
- Move the reset token out of the URL.
- Invitation list: a status badge per invitation and a confirmation before revoking.
- A setting to turn off the logo in e-mails, or to embed it in the message instead of loading it from your server.
- Profile: a single current-password field, and a translated picture button with a preview.
- Public pages: show which account the page is about.
- Refreshed screenshots for every page and e-mail.

## 12.4.2.0 and later: depth (in six months to a year, if needed)

- Access profiles (predefined library sets) for invitations.
- Links to the documentation from the tooltips.
- An end-to-end test against a real jellyfin-web.
- Accessibility review.
- A schema version for the stored data.
- Re-encoding of uploaded avatars.
- Sponsorship (to be designed): a user invites someone, or asks for an invitation that the administrator approves. The invited account gets the same libraries as the sponsor and is never an administrator.
- E-mail validation inside Quick Connect (to be designed): a button in the Quick Connect dialog sends a link by e-mail; approving it from the e-mail signs the waiting device in.
- A confirmation e-mail for addresses imported from Seerr.
- Protection against look-alike user names.
- A daily cap of e-mail change requests per user.
- Follow new Jellyfin releases (see the compatibility table in the README).

## Versioning

`<Jellyfin major>.<Jellyfin minor>.<plugin release>.<fix>`: the first two numbers are the Jellyfin version the release is built for (12.4 for Jellyfin 12.4), the third grows with each feature release on that version, the fourth with each fix. The next feature release on Jellyfin 12.4 is 12.4.1.0; a fix to it is 12.4.1.1. The exact Jellyfin version required is the `targetAbi` in `manifest.json`. A new Jellyfin version starts a new series (12.5.0.0, 13.0.0.0); older releases stay available for older servers.
