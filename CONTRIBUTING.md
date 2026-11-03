# Contributing to Mail Concierge

Thanks for your interest. Mail Concierge is maintained by one person, so small, focused pull requests are the easiest to review.

## Build and test

You need the .NET 10 SDK and, for the web client tests, Node.js 22.

```shell
dotnet build -c Release     # must produce 0 warnings
dotnet test

cd Jellyfin.Plugin.MailConcierge.Tests/Client
npm ci && node --test --test-timeout=30000
```

Warnings are treated as errors (StyleCop and other analyzers are enabled), and `.editorconfig` sets the style.

## Adding a language

See the "Adding a language" section of the README. No code change is needed: add the embedded files, run `dotnet test`, rebuild.

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/) in English: `feat(scope): ...`, `fix(scope): ...`, `docs: ...`, `test: ...`, `chore: ...`. Keep the subject short and explain the why in the body when it is not obvious.

## Pull requests

A good pull request:

- does one thing, and explains why;
- adds or updates tests (`dotnet test` and the Node tests pass);
- builds with 0 warnings;
- updates the README and `CHANGELOG.md` for user-visible changes;
- never includes secrets, real e-mail addresses or logs with tokens.

For a larger change, please open an issue first to discuss it.

## Security

Do not report vulnerabilities in public issues; see [SECURITY.md](SECURITY.md).

## Conduct

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
