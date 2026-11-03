# Adding a language

Back to the [README](../README.md).

Mail Concierge ships English, French, Spanish and German (the Spanish and German texts are not yet reviewed by native speakers: corrections are welcome). English is the fallback for every missing key. Languages are discovered from the embedded files, so adding one needs no code change. For example `it`:

1. Copy `Jellyfin.Plugin.MailConcierge/Resources/Strings/en.json` to `it.json` and translate it: the public pages and messages (`page.*`), the administration pages (`cfg.*` for the settings page, `inv.*` for the invitations page) and `language.name`, the name of the language in itself (for example `Italiano`), which fills the language lists.
2. Copy the folder `Jellyfin.Plugin.MailConcierge/Resources/Email/en` to `it` and translate the `.html` and `.txt` templates (the first line of each `.txt` is `Subject: ...`). A test fails if a language lacks a template.
3. Optionally copy `Jellyfin.Plugin.MailConcierge/Resources/Client/strings.en.json` to `strings.it.json` (profile and user-list texts of the web client). The language is offered in the profile even without it, and missing keys fall back to English.
4. Run `dotnet test`, then rebuild.

Files under `Resources` are embedded automatically.
