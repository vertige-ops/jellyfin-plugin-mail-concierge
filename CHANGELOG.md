# Changelog

## 12.0.0.0

Welcome to the first public release of **Mail Concierge**, a Jellyfin plugin that puts e-mail in charge of getting people in and getting them back in. It needs Jellyfin 12.1 and changes nothing in Jellyfin itself.

Questions, ideas and bug reports are welcome in the [issues](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge/issues).

### Highlights

- Invite people by e-mail or with a link, with a choice of libraries, validity and language
- "Forgot password" that really sends an e-mail: a PIN and a single-use link
- E-mail address in the user profile, confirmed before it is used
- Optional one-way copy of confirmed addresses to Seerr
- Rate limits, hashed single-use tokens and a strict policy on the public pages
- English, French, Spanish and German

### Invitations

Administrators invite someone from the Invitations tab, or from the invite button next to the + of the user list. The invitation goes by e-mail or as a link to copy. You pick the libraries (with "select all"), how long it stays valid and the language of the message. The invitee chooses their own user name and password on a public page and may add a profile picture. Invited accounts are never administrators.

![Invitations](https://raw.githubusercontent.com/vertige-ops/jellyfin-plugin-mail-concierge/main/docs/images/invitations.png)

### Password recovery

The native "Forgot password" button now sends an e-mail with a PIN, for the page that is already open, and a single-use link. The PIN never becomes the password. The answer looks the same, in shape and in timing, whether the account exists or has an address. After a reset you decide what happens to open sessions: none, all, or those inactive for N days.

> [!NOTE]
> Use the assignment button in the settings to give the Mail Concierge reset provider to existing users. Administrators are left out unless you tick the option, and LDAP/SSO accounts are always left out. A setting brings back the native Jellyfin behavior.

### E-mail in the profile and Seerr

Users set their address themselves, with a confirmation e-mail and their current password. The old address receives a warning with a cancel link, valid 24 hours by default, that also asks for the password. Administrators can set an address for anyone. If you use Seerr, confirmed addresses can be copied one way to the matching Seerr account, with a preview first.

### Safety by design

Per-account, per-IP and per-kind e-mail limits, PIN brute-force protection, tokens stored hashed and consumed only when a form is submitted (so a mail scanner cannot burn your link), profile pictures checked on the server, strict address validation, and no activation behind an undeclared reverse proxy. The known limits are listed openly in the [security model](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge/blob/main/docs/security-model.md).

### Before you start

- Set the **public URL** of your server in the settings: no e-mail is sent until it is set.
- Add your SMTP account and press the test e-mail button.
- Behind a reverse proxy, declare it in Jellyfin under **Known Proxies**.
- Install from the repository URL given in the [README](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge#installation), then restart Jellyfin.

### Good to know

- The Spanish and German texts have not been reviewed by native speakers yet; English is the fallback for any other language.
- The logo at the top of e-mails is loaded from your public URL; without one, no image is shown.
- Native clients (Swiftfin, Android TV...) have no "Forgot password" screen or e-mail field, so they are out of scope.

### What's included

- **Features:** invitations, password recovery, profile e-mail, optional Seerr copy.
- **Settings page:** tabs (General, SMTP, E-mail templates, Seerr, Accounts, Invitations), each with its own Save button, help tooltips, a test e-mail button, editable templates, and warnings for an insecure SMTP mode or an `http://` public URL.
- **Look:** a butler logo on the public pages and in the e-mails, and a plugin image in the catalog.
- **Packaging:** built with jprm for the Jellyfin plugin repository format.

Thank you for trying it, and enjoy a Jellyfin that remembers how to say hello.
