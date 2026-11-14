# Architecture

Back to the [README](../README.md).

Mail Concierge runs inside the Jellyfin server and changes nothing in Jellyfin itself. It creates and reads accounts only through Jellyfin's own APIs, and keeps its own data (addresses, hashed tokens, invitations) in a separate file.

![Mail Concierge inside Jellyfin: an administrator, the Jellyfin apps and invited users talk to the plugin's web layer; the plugin sends its e-mails to an SMTP server and optionally copies confirmed addresses to Seerr.](images/architecture.svg)

## Dependencies

Only the first group travels to your server: the release zip holds four DLLs. The packages Jellyfin provides are compiled against but not shipped, and the build tools never leave the repository.

![Dependencies of Mail Concierge: the plugin uses MailKit, which uses MimeKit, which uses BouncyCastle; the Jellyfin contracts and runtime come with the server; analyzers, test libraries and release tools are build only.](images/dependencies.svg)

The packages and versions shown are those of release 12.4.0.0. The exact versions are in `Jellyfin.Plugin.MailConcierge/Jellyfin.Plugin.MailConcierge.csproj` and in the `packages.lock.json` files.
