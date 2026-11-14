# Roadmap

This is a plan, not a promise: Mail Concierge is maintained by one person, and dates are intentions. E-mail is the only channel and no other is planned.

## Principles

- **Few releases.** A feature release comes out when a coherent batch is ready, never one feature at a time, so that nobody has to update all the time.
- **Fix releases only when needed**: for a security problem or a regression.
- **Bundled libraries** (MailKit, MimeKit, BouncyCastle) are updated for a security fix or a real need. Documentation and screenshots change without a release. See the [architecture and dependencies](architecture.md).
- **Security first.** Anything that touches accounts, tokens or sign-in gets a dedicated security review. A new way to sign in gets a full review and a second, independent one. Security problems are fixed first and disclosed after the fix is released (see [SECURITY.md](../SECURITY.md)).

The project overview is in the [README](../README.md).

## Delivered

### 12.4.0.0 (for Jellyfin 12.4 and later 12.x)

- Sign in with the confirmed e-mail address (off by default).
- "Forgot password" also accepts the confirmed e-mail address.
- The settings name the accounts that share a confirmed address.
- Invitation history, with an Erase button on every line.
- A series of hardening fixes from a security review. See the [changelog](../CHANGELOG.md).

### 12.0.0.x

Bug and security fixes only, for Jellyfin 12.1 to 12.3.

## 12.4.1.0: solid foundations (coming days, November 2026)

- A "Live TV" checkbox on each invitation, so that invited accounts get live TV only when you choose.
- Move the reset token out of the URL.
- Refuse to enable the plugin until the SMTP settings are valid, and reject a URL typed in the SMTP host field.
- SMTP presets (for example Google, Proton, Custom) that fill in host, port and security, with a link to the provider's official documentation.
- E-mail budget counters in the administration page, with the number of e-mails sent per user (read only).
- Invitation list: a status badge per invitation and a confirmation before revoking.
- Public pages: show which account the page is about.
- Profile: a single current-password field, and a translated picture button with a preview.
- A setting to turn off the logo in e-mails, or to embed it in the message instead of loading it from your server.
- A review of the default rights given to a new account.

## 12.4.2.0: comfort and robustness (early December 2026)

- A persistent e-mail queue with spaced retries and a visible send status per invitation. Codes are regenerated at each retry and never stored in clear.
- An optional e-mail to administrators when an e-mail limit is reached, with a report of how many e-mails each user received.
- Access profiles (predefined sets of libraries and Live TV) for invitations.
- A daily cap of e-mail change requests per user.
- Re-encoding of uploaded avatars.
- A confirmation e-mail for addresses imported from Seerr.
- Protection against look-alike user names.
- Portuguese (Brazil) and Simplified Chinese, with a native review.

## Later: new ways in (no date)

Each of these opens a new way into an account, so each gets a full security review and a second, independent one before release.

- E-mail validation inside Quick Connect (to be designed): a button in the Quick Connect dialog sends a link by e-mail; approving it from the e-mail signs the waiting device in.
- Sponsorship (to be designed): a user invites someone, or asks for an invitation that the administrator approves. The invited account gets the same libraries as the sponsor and is never an administrator.

## Ongoing, without a release

- Refreshed screenshots for every page and e-mail, and documentation.
- Links to the documentation from the tooltips.
- An end-to-end test against a real jellyfin-web, an accessibility review, and a schema version for the stored data (shipped with the next feature release).
- Follow new Jellyfin releases (see the compatibility table in the README).

## Versioning

`<Jellyfin major>.<Jellyfin minor>.<plugin release>.<fix>`: the first two numbers are the Jellyfin version the release is built for (12.4 for Jellyfin 12.4), the third grows with each feature release on that version, the fourth with each fix. The next feature release on Jellyfin 12.4 is 12.4.1.0; a fix to 12.4.0.0 would be 12.4.0.1. The exact Jellyfin version required is the `targetAbi` in `manifest.json`. A new Jellyfin version starts a new series (12.5.0.0, 13.0.0.0); older releases stay available for older servers.
