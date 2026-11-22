# Changelog

## 12.4.1.0

Welcome to release 12.4.1.0 of **Mail Concierge**. This one keeps the tokens of e-mail links out of your logs, lets you choose Live TV for each invitation, simplifies the e-mail limits, and tidies the settings page.

> [!IMPORTANT]
> **If e-mails stop after the update**: the SMTP server, the port and the sender address are now checked before Mail Concierge sends anything. Open the settings: a yellow banner says what is missing. Enter the server name only (for example `smtp.example.org`, no `https://` and no port) and a valid sender address. An install that never saved its settings now starts turned off: tick **Enable Mail Concierge** on the General tab. A new install starts turned off too, and a banner on the settings page says so.

### Highlights

- Links in e-mails that no log ever sees
- A "Live TV" checkbox on each invitation
- SMTP providers filled in for you
- Two simple daily e-mail limits, with a warning when one is reached
- A clearer invitation history and a count of e-mails per account
- Help in an (i) icon next to every field

### Links that no log ever sees

The links in e-mails (invitation, password reset, address confirmation and cancellation) now carry their token after a `#`, which browsers never send to the server: it no longer shows in the Jellyfin log, a reverse proxy log or the `Referer`. A short page reads it and posts it to the server. The reset session of the PIN flow is posted the same way. Links sent by earlier versions keep working, and the page removes their `?t=` from the address bar.

### Live TV, per invitation

A "Live TV" checkbox on each invitation: invited accounts get live TV only when you tick it. It is unticked by default, so invitations created from now on no longer give live TV unless you choose; invitations created before this version keep Jellyfin's default (the history shows a dash for them). The box only appears when your server has Live TV, and a short note under it says what an unticked box means.

### Simpler e-mail limits

Two daily limits replace the three hourly ones: **10 e-mails per recipient per day** and **200 e-mails per day for the whole server**. Invitations, password recovery and address confirmation share them; they are counted over a rolling 24 hours, in memory only, so a restart resets them. The security notices sent to a replaced address and the test e-mail are never counted, and the recipient is known only by a hash of the address.

When a limit stops an e-mail, the Jellyfin log says so and the General tab shows a banner with the date and how many recipients are at their limit. If you give an **address to warn** on the General tab, Mail Concierge also sends it a short translated e-mail, with counts only and never a recipient's address, at most once a day for each limit. The per-account limit on recovery e-mails and the per-address limits stay, but are no longer shown on the settings page.

### A clearer settings page

The explanations under the fields are now in the **(i) icon** next to each field's label: the provider's official instructions in the icon of **Provider**, the hints for the user name and the password in the icons of those fields (they follow the chosen provider), the note about saving before sending a test in the icon of the test address, and so on for every tab. Only short status messages stay visible. The icons open with the mouse, with the keyboard (Escape closes them) and by touch. Links on the page are underlined, in the colour of the text, and turn Jellyfin blue when you point at them; other Jellyfin pages are not affected.

### Upgrade notes

- Install 12.4.1.0 on Jellyfin 12.4 and later 12.x (tested on 12.4 only), then restart Jellyfin.
- **Existing installs switch the e-mail logo to "Included in the message" when they update**; choose "Loaded from the server" in the e-mail templates tab to keep the previous behaviour.
- The three hourly limits of earlier versions are no longer used and their values are not carried over: if you had changed them, set the new daily limits.
- Invitations created before this version keep Jellyfin's default for Live TV; links sent by earlier versions keep working.

### What's changed

#### 🚀 Features

- A "Live TV" checkbox on each invitation, unticked by default, shown only when the server has Live TV.
- A provider list on the SMTP tab fills in the host, port and security for Gmail, Brevo, SMTP2GO, Mailgun (EU region by default) and Mailjet, with a link to each provider's instructions and a reminder to verify the sender's domain with the provider.
- An "E-mail logo" setting in the e-mail templates tab: included in the message (the new default: it shows without a click, asks nothing from your server and adds about 30 KB to each HTML e-mail), loaded from your server as in 12.4.0.0, or left out. The test e-mail follows the saved setting.
- Two daily e-mail limits (per recipient and for the server), with a log line, a banner and an optional e-mail to an address you choose when one is reached.
- The Accounts tab shows how many e-mails each account received since Jellyfin started, with the date of the last one and the number of failures, and the totals of invitations. The counters live in memory only and are reset when Jellyfin restarts; no address, subject or error text is kept.
- On the account creation page, the picture field is a translated button with a round preview, and a picture can be removed before sending.

#### 🌟 Enhancements

- The invitation history is a table with a status column (icons for pending, accepted, expired and revoked), the access granted to each invitation (libraries and Live TV) and the date of acceptance. You can filter by status and search, and the table adapts to a phone screen. For invitations accepted from this version on, the name opens that user's page in the dashboard. Erasing a line of the history always asks first.
- The settings page refuses to enable the plugin until the public URL, the SMTP server and the sender address are complete, and the refusal says which fields to save and on which tab. A new install starts turned off, with a banner telling you to tick **Enable Mail Concierge**; installs that already have a setting keep it.
- The SMTP server field refuses a web address or a port: enter the server name only.
- The public URL is suggested from the address published in Jellyfin when the field is empty; it is only saved when you click Save.
- The explanations under the fields moved into (i) icons, and the links of the settings page follow the Jellyfin look.
- Readable on Jellyfin's light and dark themes: warnings and errors on the settings page keep the theme's text colour and are marked by a coloured bar or tinted background. On the public pages the main button is darker (white text with enough contrast) and every button and link shows a visible outline when reached with the keyboard. After creating an account the focus moves to the success title, and the rules for a user name are written under the field.
- The hint in the E-mail templates tab names real placeholders (`{{username}}`, `{{link}}`).

#### 🔒 Security

- The tokens of e-mail links travel in the part of the address after `#`, so they no longer reach the Jellyfin log, a reverse proxy log or the `Referer`.
- The daily limits keep a recipient only as a hash, never as an address; the warning e-mail to the administrator carries counts only.
- The logo included in the message uses a fixed identifier and no file name, so mail apps do not list it as an attachment.

#### 🐛 Bug fixes

- The logo included in the message is no longer listed as an attachment by mail apps.

#### 📦 Build and release

- The release workflow accepts a changed description and overview in the manifest, besides the single new release.

### That's all for this one

Questions, ideas and bug reports are welcome in the [issues](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge/issues). The known limits are listed openly in the [security model](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge/blob/main/docs/security-model.md).

<p align="center">
  <img src="https://raw.githubusercontent.com/vertige-ops/jellyfin-plugin-mail-concierge/main/docs/images/new-release-done.jpg" alt="A butler holding a sheet that reads: New release done sir!" width="360">
</p>

## 12.4.0.0

Welcome to release 12.4.0.0 of **Mail Concierge**. This one lines the plugin up with Jellyfin 12.4, lets people sign in with their e-mail address, and closes a long list of gaps found in a security review.

> [!IMPORTANT]
> This release is for Jellyfin 12.4 and later 12.x (tested on 12.4 only). Jellyfin 12.1 to 12.3 stay on 12.0.0.0, which remains available in the catalogue.

### Highlights

- Sign in with your e-mail address (off by default)
- "Forgot password" accepts the e-mail address too
- Invitation history, with an Erase button on every line
- A tidier settings page
- A security review, and everything it found

### Sign in with your e-mail address

Users can type the confirmed e-mail address of their account in place of the user name, in the web client and in every other app, since the sign-in request is the same. It is **off by default**: tick the option in the settings. The address is swapped for the user name before Jellyfin checks the password, so passwords, sessions and lockout stay Jellyfin's own. Mail Concierge never sees the password and keeps no counter.

Limits, by design:

- One account per address: an address confirmed on several accounts cannot be used to sign in, and the settings tell you how many there are and which accounts share them (user names only, never the addresses).
- Only local Jellyfin accounts: LDAP/SSO accounts and disabled accounts are left out.
- Administrators are left out unless you tick the existing option that also allows them to reset their password by e-mail.
- The user name always wins: text that is already a user name is never read as an address.
- Sign-in by address stops being offered once the account has 3 failed sign-ins, whether they were typed with the address or the user name, and earlier when the account's lockout threshold is low: it is never offered when one more failure would reach the threshold. It comes back after a successful sign-in by user name. The user name keeps working.
- A failed sign-in by address gives the same answer as an unknown user, never earlier than a fixed minimum time, so it does not reveal whether an address is known.

> [!NOTE]
> Native apps keep their "User" label on the sign-in screen; the web client shows "User name or e-mail". Throttling or banning sign-in attempts is still not Mail Concierge's job: see the [security model](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge/blob/main/docs/security-model.md).

### Forgot password, by address too

"Forgot password" also accepts the confirmed e-mail address of the account, in place of the user name; an address shared by several accounts does not work there (the user name does).

### Invitation history

The Invitations tab lists the invitation history, and every line, whatever its state, has an Erase button: it removes the invitation and stops its link, and never touches the account that was created from it.

### A tidier settings page

The General settings are reordered (public URL first), the e-mail language is now in the Templates tab, and the forgot-password switch is reworded: "Do not use Mail Concierge for Forgot password". A button clears the stored SMTP password.

### Upgrade notes

- Install 12.4.0.0 on Jellyfin 12.4 and later 12.x (tested on 12.4 only), then restart Jellyfin. Sign-in by e-mail stays off until you tick it.
- Accounts that already exist keep their own lockout threshold; only accounts created from an invitation after the upgrade get the new value of 5.
- Users who want to sign in by address need a confirmed address in their profile.

### What's changed

#### 🚀 Features

- Sign in with the confirmed e-mail address of the account, off by default.
- "Forgot password" accepts the confirmed e-mail address in place of the user name.
- Invitation history with an Erase button on every line.
- The settings name the accounts that share a confirmed address (user names only, never the addresses).
- A button clears the stored SMTP password.

#### 🌟 Enhancements

- The General settings are reordered, the e-mail language moves to the Templates tab, and the forgot-password switch is reworded.
- Invited accounts are locked out after 5 wrong passwords (instead of 3) and cannot manage live TV. User names chosen on the invitation page can no longer contain `@`; accounts created by an administrator, or older ones, may still contain one, and a user name always wins over an address.
- User names chosen on the invitation page are limited to 64 characters and cannot contain invisible characters.
- The SMTP user name is trimmed.
- One attempt to deliver an e-mail is limited to 2 minutes.

#### 🔒 Security

- The enable switch and the proxy guard now cover every public page and sending path: invitations and e-mail changes as well as recovery, and sign-in by e-mail address. While the service is closed, no token is consumed and no invitation, recovery or e-mail change message is started; messages already queued are still delivered, and the administrator's test e-mail still works.
- The e-mail sending queue is bounded: when it is full, new messages are refused instead of piling up in memory.
- Public pages (reset, confirm, cancel, accept) are limited to 30 views per five minutes and per client address.
- The hourly e-mail limits keep holding when requests come from a very large number of client addresses.
- A failed sign-in by e-mail address is answered at the same minimum time whatever the failure.
- The password checks of the forms can no longer lock an account on their own; a wrong password typed on Jellyfin's own sign-in at the same moment can still add the last failure.
- The tokens of a new invited account are revoked before the account is enabled.
- A recovery e-mail requested at the very moment an address is changed can no longer go to the previous address.
- Profile pictures: the JPEG check now reads every segment before the image data, so a second or malformed header can no longer slip through.
- Seerr: the size of its responses is capped and each request has a single time limit.
- The Seerr import only stores the addresses shown in the preview, starts with every row unticked, warns that imported addresses count as confirmed, and gives up on a Seerr that returns too many users or answers too slowly.
- A proxy on the shared range 100.64.0.0/10 is now seen, and public page views are watched by the proxy guard too.
- The SMTP password and the Seerr API key are asked for again when their server changes. The SMTP password is also asked for again when encryption is turned off.

#### 🐛 Bug fixes

- "Regenerate" on an invitation sends the e-mail again when the invitation has an address.
- The e-mail change form no longer says the message was sent when it could not be queued.
- The settings tab that is open is the one highlighted, also when the page is opened from the invite icon of the Users page.

#### 📦 Build and release

- The release workflow checks that the manifest only adds the release being published.
- The release workflow builds without write access, checks the dependencies of the build tool by hash, and never replaces an already published release.

### That's all for this one

Questions, ideas and bug reports are welcome in the [issues](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge/issues). The known limits are listed openly in the [security model](https://github.com/vertige-ops/jellyfin-plugin-mail-concierge/blob/main/docs/security-model.md).

<p align="center">
  <img src="https://raw.githubusercontent.com/vertige-ops/jellyfin-plugin-mail-concierge/main/docs/images/new-release-done.jpg" alt="A butler holding a sheet that reads: New release done sir!" width="360">
</p>

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
