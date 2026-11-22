# Frequently asked questions

**Do I need to host anything besides Jellyfin?** No. Mail Concierge is a plugin that runs inside Jellyfin. You only need an SMTP account to send e-mails.

**Which Jellyfin versions are supported?** Release 12.4.x is built for Jellyfin 12.4 and later 12.x (tested on 12.4 only). Release 12.0.0.0 covers Jellyfin 12.1 to 12.3. Older Jellyfin versions are not supported. See the compatibility table in the [README](../README.md#tested-with).

**Which SMTP provider should I use?** Any provider that gives you an SMTP host, a port and credentials. The provider list on the SMTP tab has presets for Gmail, Brevo, SMTP2GO, Mailgun and Mailjet: it fills in the host, port and security, and links to the provider's instructions. Gmail requires an app password instead of your normal one. Brevo, SMTP2GO, Mailgun and Mailjet require you to verify the sender's domain with them, otherwise messages may land in spam. Use the test e-mail button before anything else. Then tick **Enable Mail Concierge** on the General tab: a new install starts turned off, and until it is enabled invitations and e-mail recovery do not work.

**Why does it say it refuses to activate behind my proxy?** Jellyfin must know your reverse proxy to read the real client address. Add it to Known Proxies (Dashboard, Networking). The README troubleshooting section explains the guard and its caveats.

**Can an invited person become an administrator?** No. Invited accounts are never administrators. Administrators are also excluded from password recovery and from sign-in by e-mail unless you tick the option.

**What can an invited account do?** Mail Concierge only closes rights on the account it creates: it is not an administrator, it cannot manage Live TV, it can watch Live TV only if the invitation asks for it (the box is shown only when your server has Live TV; invitations created before 12.4.1.0 keep Jellyfin's default, and the history shows a dash for them), it sees only the libraries ticked in the invitation, it is locked out after 5 wrong passwords, and its password reset is handled by Mail Concierge. Everything else is Jellyfin's default for a new user, identical to an account created by hand: content downloading, media conversion and sync transcoding are allowed, public sharing is allowed, there is no bitrate or session limit, remote access with live transcoding is on, and there is no parental rating. To change any of this for one person, open Dashboard, Users, the user, then Profile, Access, Parental Control or Playback; Mail Concierge does not override changes you make afterwards.

**Does the PIN in the recovery e-mail become the password?** Never. It opens the native "Forgot password" page, and the person then chooses a new password. The e-mail also has a single-use link.

**Does it work with the Jellyfin mobile and TV apps?** Native clients do not load the web script, so they have no e-mail field and no PIN screen. People should use the link in the e-mail. Entering a PIN in a native screen fails safely.

**What about LDAP or SSO accounts?** They are always excluded from Mail Concierge recovery.

**Why e-mail only? Can you add SMS, Telegram or push?** No, and none is planned: one channel, done carefully, is the whole point.

**What happens to my data if I uninstall the plugin?** The configuration file with invitations and addresses stays in Jellyfin's plugin data folder until you delete it. An address already copied to Seerr stays there.

**How is it different from jfa-go or Wizarr?** Those are separate services with their own interface and broader features. Mail Concierge is one plugin with a narrow scope. The README compares them.

**How is the plugin developed?** With AI assistance; see "How it is built" in the [README](../README.md#how-it-is-built).

**How do I report a security problem?** Do not open a public issue: see [SECURITY.md](../SECURITY.md).
