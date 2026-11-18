# Plan: service accounts and short-lived tokens

**Status:** built locally (2026-11-18), unreleased: Server and console 3.0, CLI 3.0, `@winche-technologies/winche-admin` 3.1.0
(`initializeAdmin({ key })` / `identityToken`). The server's limits are `Winche__ServiceAccountKeyMaxDays` and
`Winche__ServiceAccountKeysAllowed`. Not built: source-address limits (an open question). The admin SDK (`docs/admin-sdk.md`) uses these for other servers and CI; it only needs an access
token, however it was issued.

## Why

Today a project has CI tokens: made by an admin, named, limited to deploy scopes (`functions`, `rules`, `indexes`,
`assets`), expiring in 30 days to 2 years, and each one is the bearer secret itself. That leaves:

- **Long-lived secrets sent with every call:** one leaked from a log or a build output works until it expires.
- **Secrets copied into every CI system and server,** and rotated by hand.
- **Tied to their maker:** a token stops working when its maker is no longer an admin. That closes access for someone
  who leaves, but also breaks a production integration they once set up.
- **Deploy only:** another server can't read or write the project's data.

## The model

A project has **service accounts**: named (`billing-server`, `ci-deploy`), each with a role (viewer, member, admin) or
deploy scopes. An account belongs to the project, not to whoever made it; the history says who made and changed it.
Disabling an account stops all its access at once.

An account proves who it is with one of:

1. **Trusted identity (no stored secret):** a CI system or cloud host that issues signed OpenID Connect tokens about
   its jobs or machines. The account trusts an issuer and a subject pattern (e.g. a repository's main branch). The
   caller exchanges its own token for a Winche access token; no Winche secret exists anywhere.
2. **Keys** (for servers that can't): a secret shown once. At most two active per account, so one is replaced with no
   downtime.

Either way the caller exchanges it for an **access token** lasting an hour (signed by the server, carries the account,
project and role). Only access tokens are sent with API calls; the SDK and the CLI exchange and refresh them
themselves.

```
POST /console/v1/projects/<id>/service-accounts/token
  { "key": "wsk_…" }                       → { "accessToken": "…", "expiresAt": "…" }
  { "assertion": "<the caller's OIDC token>" }
```

## Maintenance

- **Each key:** created by, created at, last used (when and from which address), expiry (required; at most 2 years).
  Keys unused for 90 days are flagged; an admin can set them to expire unused.
- **Rotation:** add a second key, move the server to it, revoke the first. The console shows which key each call used
  recently, so it's clear when the old one is no longer in use.
- **History:** every call is attributed to the account and the key or identity (`billing-server (key 2)`,
  `ci-deploy (main, pipeline 4812)`).
- **When an admin is removed** (or is no longer an admin): the console lists the accounts and keys they made and asks
  for each: keep, revoke, or replace the key. Nothing stops working on its own.
- **Server settings:** the longest key life and whether keys are allowed at all (a server may allow trusted identities
  only).

## Replacing CI tokens

Each CI token becomes an account with deploy scopes and one key (the token's secret is the key; it keeps working until
it expires). `WINCHE_TOKEN` in CI keeps working: the CLI exchanges it for an access token. New CI setups use a trusted
identity instead.

## Steps

1. **Accounts and keys:** accounts with roles or scopes; keys hashed, at most two, with expiry and last use; the token
   exchange; access tokens checked by the console's project API. Console: an Accounts page (list, create, keys,
   disable). CLI: `winche accounts`, `accounts:create`, `accounts:keys:add|revoke`.
2. **Trusted identities:** an account trusts an issuer and subject pattern; the exchange checks the caller's token
   (discovery, keys, audience, expiry). The CLI in CI exchanges the job's own token.
3. **Maintenance:** unused-key flags, the removed-admin review, history attribution, server limits.
4. **CI tokens migrated** to accounts; the old token endpoints removed.
5. **Docs and release.**

## Open questions

- Should a key be limitable to source addresses?
- Should access tokens be shorter (15 minutes) for admin-role accounts?
