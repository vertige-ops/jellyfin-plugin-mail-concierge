# Security policy

## Supported versions

Mail Concierge is maintained by one person. Only the latest released version receives security fixes.

| Version | Supported |
| ------- | --------- |
| Latest release | Yes |
| Older releases | No, please update |

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private reporting: open the **Security** tab of this repository and choose **Report a vulnerability**. This creates a private advisory visible only to you and the maintainer.

Please include:

- the Mail Concierge and Jellyfin versions, and whether a reverse proxy is in front of Jellyfin;
- what an attacker can do and the steps to reproduce it;
- logs or requests with any token, password, API key or address redacted.

## What to expect

This is a volunteer project, so these are goals, not guarantees:

- acknowledgement within about 7 days;
- an assessment and a plan within about 30 days;
- a fix released as soon as it is ready, with credit in the changelog if you want it.

Please give the maintainer reasonable time to release a fix before disclosing the problem publicly.

## Scope

In scope: the Mail Concierge plugin code, its public pages and endpoints, and the release workflow. Out of scope: vulnerabilities in Jellyfin itself, in your reverse proxy or in your SMTP provider (report those to their own projects), and actions that already require Jellyfin administrator rights, which are trusted.
