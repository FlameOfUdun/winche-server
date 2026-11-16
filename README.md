# Winche Server

The whole Winche stack in one container: documents (REST and live), files, users and sign-in, functions, webhooks and
logs, set up and run from its built-in **console**. Only what the server needs to start is configuration. Everything
else is changed in the console, applies within seconds on every server, and is kept in a change history. That covers
rules, sign-in methods, identity providers, indexes, where files are kept, webhooks and who may do what.

## Quickstart

You need Docker. The image is `ghcr.io/flameofudun/winche-server` (amd64 and arm64), tagged by version: `1` follows the
latest 1.x, `1.0` the latest 1.0.x, `1.0.0` stays put. The compose file in this repository uses `1`; copy it (with
nothing else) to wherever the server will run.

1. **Configure.** Create a `.env` file beside `docker-compose.yml`:

   ```sh
   WINCHE_MASTER_KEY=...        # openssl rand -base64 48 (back it up: see Backups)
   POSTGRES_PASSWORD=...        # openssl rand -base64 24
   WINCHE_PUBLIC_URL=http://localhost:8080
   ```

2. **Start it:** `docker compose up -d`, then open http://localhost:8080. That's the console.

3. **Set it up.** First-run setup asks for the **setup code**, which only someone who can see the server's log has:

   ```sh
   docker compose logs winche | grep "Setup code"
   ```

   Then it walks you through:
   - your admin account, a server admin with a password;
   - two-step sign-in, with an authenticator app (you can do it later);
   - your company sign-in, optional: Keycloak, Microsoft Entra ID, Google Workspace, Okta or any OpenID Connect
     provider, with a test sign-in;
   - your first project: its name, its id, and **its database**. That's a database of its own, either on this
     server's PostgreSQL or on another PostgreSQL server (checked before it's used). The server's own database never
     holds a project's data.
   - your first app, optional.

   Or do all of it from a terminal, without the console: `winche setup --url http://localhost:8080` (the
   [CLI](https://github.com/FlameOfUdun/winche-cli) asks for the setup code, your account and the first project, and
   signs you in). Products, settings (as files), CI tokens and more projects have CLI commands too.

4. **Start a product.** Every product (Authentication, Database, Storage, Functions) is off until you start it from its
   page (**Get started**). Its API refuses requests until then (`failed-precondition`, reason `service-disabled`).
   Database and Storage start **locked**, with nothing allowed until you publish rules, or in **test mode**, with
   everything allowed for 30 days.

5. **Connect an app.** In **Project settings → General → Add app**, register a Flutter app and download its
   `lib/winche_options.dart` (or run `winche configure --url <your server>` in the app's folder), then:

   ```dart
   await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);
   ```

   Flutter is the only SDK so far. The API is plain HTTP for anything else: documents under `/documents/v1`, files under
   `/files/v1`, sign-in under `/auth/v1`, functions under `/functions/v1`. `GET /.well-known/winche` describes it
   (nothing secret).

Run it behind HTTPS (a proxy or load balancer) for anything but trying it out: it trusts the `X-Forwarded-*` headers.

## Projects

A server runs any number of projects. The first is at the root (`https://api.example.com/_console`); the others are under
`/p/<id>` (`https://api.example.com/p/wards/_console`, apps at `https://api.example.com/p/wards`). Add one from the project
picker in the console's top bar. A project can be **archived**: its apps and console are refused (`410`) until a server
admin restores it, and nothing is deleted. Archived projects can be **deleted** for good, which removes their database
and files.

Each project has its own members (Project settings → Members). Server admins can use every project.

## Configuration

Environment variables (or `appsettings.json`, section `Winche`):

| Setting | |
| --- | --- |
| `Winche__ConnectionString` | PostgreSQL: the **server's database**. It holds the console accounts (schema `console_accounts`), the server settings, the list of projects, and each project's configuration in a schema of its own, `project_<id>` (settings, secrets, rules, apps, members, tokens, function versions). Projects' data is never here. A project on "this server's PostgreSQL" gets a database named `<this database>_p_<id>`, so this user needs `CREATEDB`. |
| `Winche__MasterKey` | At least 32 random characters. Encrypts the secrets saved in the console; the keys that sign tokens and file links come from it. **Back it up**: losing it loses those. Use the same value on every server. |
| `Winche__PreviousMasterKeys__0` | Only while changing the master key: the old one. On start the server moves everything to the new key and logs when it's done ("Remove Winche__PreviousMasterKeys and restart"). |
| `Winche__PublicUrl` | Where apps reach the server (tokens' issuer, file links), e.g. `https://api.example.com`. Set here only. |
| `Winche__SetupCode` | Optional: the code first-run setup asks for. Unset, the server makes one and prints it in its log (`Setup code: …`) until someone has set it up. A wrong code takes a second to be refused. |
| `Winche__ConsoleSignIn__Issuer`, `__ClientId`, `__ClientSecret` | Optional: console sign-in through an OpenID Connect provider from the start (also `__ButtonLabel`, `__RoleClaim`, `__AdminValues__0`, `__MemberValues__0`, `__ViewerValues__0`, `__PasswordsOnlyForAdmins`). It becomes the first of the console's identity providers; more are added in **Server settings → Authentication → Sign-in method**. Register `<PublicUrl>/_console/api/auth/oidc/callback` at each provider, and optionally `<PublicUrl>/_console/api/auth/oidc/backchannel-logout` as its back-channel logout URL. |
| `Winche__RequireTwoFactorForSettings` | Default `true`: changing settings needs two-step sign-in (a trusted provider's own second step counts). Set `false` only to try the server out. Also in **Server settings → Authentication → Sign-in method**. |
| `Winche__ProjectsPath` | Projects' files on disk, each under `<id>/files`, until they're moved to a bucket (default `/data/projects`: mount a volume). |
| `Winche__FilesPath` | Optional: keeps an existing first project's files in their own folder. |
| `Winche__ConsolePath` | Where the console is served (default `/_console`). |
| `Winche__ProjectId`, `Winche__ProjectName` | Defaults for the first project as apps see it (default `winche`). Setup asks for its own. |

Several servers can share one database: settings, rules and webhooks follow within seconds, and console sign-ins
carry over (their keys are kept in the database). `GET /healthz` answers 200 once the database does.

## The console

- **Overview:** each product with its numbers, recent changes, your apps.
- **Authentication:** your apps' users, sign-in methods (email and password, email links, guests, Google, Apple, any
  OpenID Connect provider, optionally kept in step with Keycloak), trusted token issuers, email templates, webhooks,
  sessions and signing keys.
- **Database:** a data browser that refreshes as apps write, plus rules (an editor with a simulator), indexes and
  expiry (a table or text), change history, webhooks, logs and settings.
- **Storage:** files, rules, webhooks, logs and settings (size limit, a disk or an S3-compatible bucket).
- **Functions:** TypeScript functions run by Deno: callables apps call, and triggers on documents, files and users.
  You write them in your editor and deploy them with the CLI (`winche functions deploy`, or a CI token). The console
  lists them, runs callables and shows each trigger's runs and versions.
- **Logs:** every product's requests and events, as plain sentences, with filters and saved queries. Changes are always
  logged; successful reads are sampled.
- **Project settings:** the project (name, id, image), apps and browser origins, members and invites, CI tokens, and
  **Data** (where its database is, moving it, connections). The CLI signs in with `winche login`, once per server, as
  a session of your account like the console's own.
- **Server settings** (server admins): the console's own accounts. Users, sign-in method (email and password, which is
  always there, plus identity providers with role mapping and Keycloak sync), email templates, and settings (sessions,
  lockout, the SMTP server for console emails, signing keys).

Settings are saved with a reason, as versions you can restore. A save made from an older version than the latest is
refused, so two admins can't undo each other's changes.

Console emails (invites, password resets) go through the SMTP server set in **Server settings → Authentication →
Settings → Emails to users**. Until there is one, invites are shared as links, and a server admin sets a new password
for someone who forgot theirs (Server settings → Authentication → Users).

## Products

Starting a product runs its setup on the server, step by step, with the console following along: its tables,
functions and indexes, and Authentication's first signing key. Nothing is made in a project's database for a product
that isn't started. Until a started product is set up, its API answers `503 unavailable` (`service-starting`, or
`service-setup-failed` naming the step and why). Started products are set up again, quickly, each time a project starts.

Each product has a **Webhooks** tab: call an address of yours when its documents, files or users change, signed and
retried. To run code on the server instead, write a function with that trigger.

## Where data is kept

Each project has one database for its data: documents, files' records, app users, webhook deliveries and logs.
**Project settings → Data** (server admins) shows where it is and moves it, to a new or empty database on this server's
PostgreSQL or on another server (host, user, password, SSL), checked first. During a move the project pauses: its apps
are told to try again (`503`, `project-moving`) while every table is copied and its rows are counted on both sides.
Then it carries on in the new database. If anything goes wrong, it carries on where it was. The old copy stays until you
finish the move (delete it, or keep it). The same page sets the project's connections (how many at once, the command
timeout).

## Backups

Three things make up a Winche Server:
- the **databases**: the server's database, and each project's;
- the **files**, unless they've been moved to a bucket;
- the **master key**.

Back up all three. Without the master key, a backup can't decrypt the secrets saved in the console: SMTP and Keycloak
passwords, bucket keys, webhook signing secrets. It also can't decrypt the console's sign-in keys, and it invalidates
sign-in tokens and file links.

With the compose file (project name from its folder, here `winche-server`):

```sh
# Databases: compressed dumps, safe to take while the server runs (the server's, then each project's)
docker compose exec -T postgres pg_dump -U winche -Fc winche > winche-$(date +%F).dump
docker compose exec -T postgres pg_dump -U winche -Fc winche_p_clinic > winche_p_clinic-$(date +%F).dump

# Files: the /data volume
docker run --rm -v winche-server_files:/data -v "$PWD":/backup alpine tar czf /backup/files-$(date +%F).tgz -C /data .

# Master key: keep a copy of .env (or wherever WINCHE_MASTER_KEY lives) somewhere safe and separate
```

Restore into a fresh stack (same `.env`, so the same master key):

```sh
docker compose up -d postgres
docker compose exec -T postgres pg_restore -U winche -d winche --clean --if-exists < winche-2026-11-12.dump
docker compose exec -T postgres createdb -U winche winche_p_clinic
docker compose exec -T postgres pg_restore -U winche -d winche_p_clinic < winche_p_clinic-2026-11-12.dump
docker run --rm -v winche-server_files:/data -v "$PWD":/backup alpine sh -c "cd /data && tar xzf /backup/files-2026-11-12.tgz"
docker compose up -d
```

Files moved to S3 or R2 aren't in the volume: protect them with the bucket's own versioning or replication. On a
managed database (RDS, Azure, Cloud SQL), its automated backups replace the `pg_dump` step. A project on another
PostgreSQL server is backed up there.

## Upgrading

Pull or build the new image and restart: the databases are brought up to date when the server starts. Take a backup
first. Several servers can be replaced one at a time.

## Working on it

The console's API is part of this repository (`src/Winche.Console`, tested in `tests/Winche.Console.Tests`). Its web
app lives in [winche-console](https://github.com/FlameOfUdun/winche-console); the server embeds a build of it from
`src/Winche.Console/wwwroot`, and `src/Winche.Console/console-web.json` says which commit. After changing the web app:

```sh
scripts/update-console-web.sh            # builds ../winche-console and copies the files in
```

then commit what changed here. `samples/` has a local PostgreSQL, MinIO and Keycloak for development.

The image carries `THIRD-PARTY-NOTICES.md`: the notices that the licenses of what it ships ask for (Deno, the server's
NuGet packages, the console's npm packages). After changing dependencies or the Deno version:

```sh
scripts/update-notices.py                # publishes the server, reads ../winche-console, rewrites the file
```

## Building the image yourself

`docker build -t winche-server:local .` builds it from this repository; set `WINCHE_IMAGE=winche-server:local` in `.env`
for the compose file to use it. To build against Winche libraries not yet on nuget.org, put their `.nupkg` files in
`packages/` (ignored by git): `nuget.config` reads that folder too.

Releases: pushing a `v*` tag (matching `<Version>` in `Directory.Build.props`) tests the server and publishes the image
for amd64 and arm64.
