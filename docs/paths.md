# Plan: one layout for every path (Winche Server 3.0)

**Status:** plan, not started.

## Why

Today the server answers on paths that grew product by product:

- The console web app is served twice: `/_console` (the server's) and `/p/<id>/_console` (each project's).
- Its API is split the same way: `/_console/api/…` and `/p/<id>/_console/api/…`, with no version, though the CLI and CI
  tokens depend on it.
- The same thing has two names: apps use `documents/v1`, `files/v1`, `auth/v1`; the console calls them `database`,
  `storage`, `app-auth`.
- Internal paths are marked with an underscore, unevenly: `_console`, and `_files` beside `files/v1`.
- Admin routes sit inside the apps' paths, so keeping the console private means patterns across every project.

## The layout

Two trees: `/console` for people and admin tools, `/projects/<id>` for apps.

```
/                                         → redirect to /console
/livez  /healthz                          health

/console                                  the console (its pages)
/console/projects/<id>/…                  a project's pages
/console/v1/…                             the admin API (below)

/projects/<id>/documents/v1/…             apps: database (REST, and ws)
/projects/<id>/files/v1/…                 apps: storage (and its signed links)
/projects/<id>/auth/v1/…                  apps: Winche Auth
/projects/<id>/functions/v1/…             apps: functions
/projects/<id>/assets/v1/…                apps: Winche Assets
/projects/<id>/.well-known/winche         app setup
/projects/<id>                            → redirect to /console/projects/<id>
```

- **Protecting the console:** one rule on `/console*`; everything under `/projects/*` stays open to apps.
- **One console, served once.** Every path under `/console` that isn't `/console/v1/…` (or a later `v2`) is one of its
  pages; nothing else falls back to it, so a mistyped app call answers 404, never the console's page.
- **Reserved:** at the root, `console`, `projects`, `livez`, `healthz`, `.well-known`; inside a project, only the
  products' names. A project id can be anything else.
- **The admin API is versioned** (`/console/v1`), like the products: the console, the CLI and CI tokens all use it.
- **The same words in both trees:** a project's admin side uses the products' names, so
  `/console/v1/projects/<id>/documents` manages what `/projects/<id>/documents/v1` serves.

### The admin API (`/console/v1`)

Server:

| Path | What |
| --- | --- |
| `session/…` | Console sign-in: `state`, `setup`, `setup/code`, `login`, `login/2fa`, `login/recovery`, `logout`, `forgot-password`, `reset-password`, `profile`, `password`, `config`, `2fa/setup\|enable\|disable`, `oidc/start\|callback\|backchannel-logout` |
| `cli/…` | CLI sign-in: `device`, `device/poll`, `device/{userCode}`, `approve`, `token`, `refresh`, `logout` |
| `tokens…` | CI tokens (list, create, `{id}`, `current`) |
| `accounts…`, `invites…` | Console accounts and invites |
| `server/…` | Server settings (`settings`, `settings/history`, `secrets/{name}`, `console-sign-in/test`), and the accounts' sign-in settings (`server/auth/…`) |
| `overview`, `logs/…` | The server's |
| `projects…` | List, create, `check-database`; `{id}/archive`, `{id}/restore`, `{id}` (delete) |

A project (`/console/v1/projects/<id>/…`): the server forwards these into that project's app, as it does for the
apps' paths.

| Path | What | Today's |
| --- | --- | --- |
| `project…` | The project, its `image`, `products/{product}/start\|setup` | `api/project…` |
| `members…` | Members | `api/members…` |
| `overview`, `logs/…` | The project's | same |
| `documents/…` | `collections`, `documents/{path}`, `query`, `history` | `api/database/…` |
| `files/…` | `browse`, `files/{path}`, `directories/{path}`, `upload-url`, `confirm`, `download-url`, `metadata` | `api/storage/…` |
| `auth/…` | The app's users, email templates, keys, providers, sign-in settings and history | `api/app-auth/…` |
| `functions/…` | Versions, `deploy`, `resources/…`, rollback, `secrets/…`, `run`, `{name}/instances` | `api/functions/…` |
| `pages/…` | The project's own console pages: list, `{name}/open`, `actions/{name}`, `{name}/view/{token}/{**path}` | `api/console-pages/…` |
| `assets/…`, `hooks/…`, `apps/…` | Same as today | same names |
| `rules/{product}/…` | Live, versions, deploy, revert, validate, simulate; `rules/text/parse\|format` | `api/rules/{sys}/…` |
| `secrets…` | List, `sources`, `test`, `keys/{name}` | `api/secrets…` |
| `settings/…` | Settings areas, `database/indexes`; `location/files` (storage's location), `location/database` | `api/settings/…`, `api/settings/storage/location`, `api/data-location` |

### Apps (`/projects/<id>`)

Unchanged below the project, except storage's signed links:

| Path | Today's |
| --- | --- |
| `documents/v1/{path}`, `documents/v1:runQuery` and the other verbs, `documents/v1/ws`, `documents/v1/ping` | same under `/p/<id>` |
| `files/v1/{path}` and its verbs, `files/v1/ping` | same under `/p/<id>` |
| `files/v1/signed/o/{**path}`, `files/v1/signed/p/{uploadId}/{part}` (links to files kept on the server's disk) | `/p/<id>/_files/o/…`, `/p/<id>/_files/p/…` |
| `auth/v1/…` (with `auth/v1/.well-known/jwks.json` and `openid-configuration`, which OpenID Connect requires under the issuer) | same under `/p/<id>` |
| `functions/v1/call/{name}`, `functions/v1/http/{name}[/…]` | same under `/p/<id>` |
| `assets/v1/channels/{channel}[/files/{**path}]`, `assets/v1/releases/{n}/files/{**path}` | same under `/p/<id>` |
| `.well-known/winche` | same under `/p/<id>` |

## What changes beyond paths

- **Winche Auth's issuer** becomes `<PublicUrl>/projects/<id>/auth/v1`. Tokens issued before the upgrade name the old
  issuer (`…/p/<id>/auth/v1`): 3.x accepts both for each project, so nobody is signed out; refreshed tokens carry the
  new one.
- **Sign-in callbacks registered elsewhere** (the console's OpenID Connect provider, a project's Google/Apple/OIDC
  providers): the new callback paths need registering at each provider. The old ones keep working through 3.x
  (redirects), and the console's settings pages show the URL to register, old and new, until the old one is unused.
- **Links already handed out** (file download links, signed disk links, email action links, invite links) keep working
  through the redirects.
- **Cookies:** the console's session cookie gets path `/console`; apps never receive it.
- **`Winche__ConsolePath`** goes: the console is always `/console`. (`Winche__PublicUrl` stays the host's address.)
- **The app-token check** (2.3.1: everywhere but the console) becomes: only under `/projects/*`, excluding nothing
  else, since the console isn't there any more.

## Redirects (all of 3.x)

| Old | New | How |
| --- | --- | --- |
| `/_console[/…]` (pages) | `/console[/…]` | 308 |
| `/_console/api/…` | `/console/v1/…` (mapped as in the tables above) | 308 (method and body kept) |
| `/p/<id>/_console[/…]` (pages) | `/console/projects/<id>[/…]` | 308 |
| `/p/<id>/_console/api/…` | `/console/v1/projects/<id>/…` (mapped) | 308 |
| `/p/<id>/_files/…` | `/projects/<id>/files/v1/signed/…` | 308 |
| `/p/<id>/…` (everything else) | `/projects/<id>/…` | 308 |
| The live socket `/p/<id>/documents/v1/ws` | `/projects/<id>/documents/v1/ws` | served at both (WebSocket clients don't follow redirects) |

Each redirect is logged once per client and path at information level ("old address used: …"), so an operator can see
what still calls them before 4.0 removes them.

## Everything that moves with it

| Where | Change |
| --- | --- |
| Winche Server | The router (`/projects/<id>` and `/console`), the console mounted once, the admin API under `/console/v1` with project calls forwarded, the redirect table, the issuer alias, the app-token check, health and readiness unchanged. README, Fargate example, upgrading notes. Version 3.0. |
| Console web app | One base (`/console`), project routes `/console/projects/<id>/…`, its API calls to `/console/v1/…`; the project switcher shows the new app address (`…/projects/<id>`). |
| CLI | `winche.json`: `"server": "https://host"` and `"project": "<id>"` (today a console URL per project); an old `"console"` value is still read and rewritten on the next save. Sign-ins kept per server. Calls go to `/console/v1`. Emulator: the same layout. Version 3.0. |
| VS Code extension | Any console links it opens. |
| App SDKs (Dart) | No code change: they take the base URL. READMEs and examples: `…/projects/<id>`. |
| Functions | `ctx` addresses are internal; nothing to change. Console pages' iframe address moves with `pages/…`. |
| Docs and samples | Every `…/p/<id>` and `/_console` in READMEs, the server's Quickstart, the CLI's and the extension's docs. |

## Steps

1. **Server routing:** `/projects/<id>` beside `/p/<id>` (both served), `/console` beside `/_console`, the admin API at
   `/console/v1` with the project forwarding, the issuer alias. Tests: every path in the tables answers on its new
   address; old addresses still answer.
2. **Redirects:** old addresses become 308s to the new ones (the socket stays served at both); the once-per-client log
   line. Tests: each row of the redirect table.
3. **Console web app** on the new layout.
4. **CLI** on `server` + `project`, reading old configs; the emulator on the new layout.
5. **Docs, samples, extension;** the console's settings pages show both callback URLs.
6. **Release:** Server 3.0 (ghcr), CLI 3.0, the extension; SDK README updates. Upgrading notes: re-register callback
   URLs at providers; update app base URLs at the next app release (old ones keep working through 3.x).

## Open questions

- 4.0 removes the redirects and the old issuer alias: on a schedule, or when the server's log shows nothing uses them?
- Should `/projects/<id>` (no product) answer apps with something useful (the project's name and products, like
  `.well-known/winche`) instead of redirecting to the console?
