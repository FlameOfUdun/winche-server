# Winche Server

The whole Winche stack in one container: documents (REST and live), files, users and sign-in, functions, assets, webhooks and
logs, set up and run from its built-in **console**. Only what the server needs to start is configuration. Everything
else is changed in the console, applies within seconds on every server, and is kept in a change history. That covers
rules, sign-in methods, identity providers, indexes, where files are kept, webhooks and who may do what.

## Quickstart

You need Docker. The image is `ghcr.io/flameofudun/winche-server` (amd64 and arm64), tagged by version: `2` follows the
latest 2.x, `2.0` the latest 2.0.x, `2.0.0` stays put. The compose file in this repository uses `2`; copy it (with
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
     provider, with a test sign-in.

   That's the server set up: it runs with no projects. **Add a project** on its Projects page: its name, its id, and
   **its database**, a database of its own either on this server's PostgreSQL or on another PostgreSQL server (checked
   before it's used). The server's own database never holds a project's data.

   Or do it from a terminal, without the console: `winche setup --url http://localhost:8080` (the
   [CLI](https://github.com/FlameOfUdun/winche-cli) asks for the setup code and your account, and signs you in), then
   `winche projects:create clinic --name Clinic`. Products, settings (as files), service accounts have CLI commands too.

4. **Start a product.** Every product (Authentication, Database, Storage, Functions, Assets) is off until you start it from its
   page (**Get started**). Its API refuses requests until then (`failed-precondition`, reason `service-disabled`).
   Database and Storage start **locked**, with nothing allowed until you publish rules, or in **test mode**, with
   everything allowed for 30 days.

5. **Connect an app.** In **Project settings → General → Add app**, register a Flutter app and download its
   `lib/winche_options.dart` (or run `winche configure --url <your server>` in the app's folder), then:

   ```dart
   await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);
   ```

   Flutter is the only SDK so far. The API is plain HTTP for anything else, under the project's address
   (`/projects/<id>`): documents under `/projects/<id>/documents/v1`, files under `…/files/v1`, sign-in under
   `…/auth/v1`, functions under `…/functions/v1`, assets under `…/assets/v1`. `GET /projects/<id>/.well-known/winche`
   describes it (nothing secret).

Run it behind HTTPS (a proxy or load balancer) for anything but trying it out. It believes the `X-Forwarded-*` headers
of proxies on loopback and private networks; a proxy that connects from elsewhere (a cloud load balancer, a CDN in
front of the server) needs `Winche__TrustedProxies` (below), or console sign-in fails.

## Projects

A server runs any number of projects, none to start with. Two addresses:

| Address | What |
| --- | --- |
| `/console` | The console: the projects, accounts and Server settings, and each project's pages at `/console/projects/<id>`. Its API is `/console/v1/…` (a project's at `/console/v1/projects/<id>/…`), which the CLI and CI tokens use too. |
| `/projects/<id>` | A project's apps: `documents/v1`, `files/v1`, `auth/v1`, `functions/v1`, `assets/v1`, `.well-known/winche`. |

To keep the console private, restrict `/console*` (and `/`, which redirects there) at the proxy or load balancer, and
leave `/projects/*` open to apps. Every project is alike: add one on the Projects page. Any project can be
**archived**: its apps and console are refused (`410`) until a server admin restores it, and nothing is deleted.
Archived projects can be **deleted** for good, which removes their database and files.

Each project has its own members (Project settings → Members). An account is a server admin or not: server admins use
every project and make projects; everyone else uses the projects they're members of.

## Configuration

Environment variables (or `appsettings.json`, section `Winche`):

| Setting | |
| --- | --- |
| `Winche__ConnectionString` | PostgreSQL: the **server's database**. It holds the console accounts (schema `console_accounts`), the server settings, the list of projects, and each project's configuration in a schema of its own, `project_<id>` (settings, secrets, rules, apps, members, tokens, function versions). Projects' data is never here. A project on "this server's PostgreSQL" gets a database named `<this database>_p_<id>`, so this user needs `CREATEDB`. |
| `Winche__MasterKey` | At least 32 random characters. Encrypts the secrets saved in the console; the keys that sign tokens and file links come from it. **Back it up**: losing it loses those. Use the same value on every server. |
| `Winche__PreviousMasterKeys__0` | Only while changing the master key: the old one. On start the server moves everything to the new key and logs when it's done ("Remove Winche__PreviousMasterKeys and restart"). |
| `Winche__PublicUrl` | Where apps reach the server (tokens' issuer, file links), e.g. `https://api.example.com`. Set here only. |
| `Winche__SetupCode` | Optional: the code first-run setup asks for. Unset, the server makes one and prints it in its log (`Setup code: …`) until someone has set it up. A wrong code takes a second to be refused. |
| `Winche__ConsoleSignIn__Issuer`, `__ClientId`, `__ClientSecret` | Optional: console sign-in through an OpenID Connect provider from the start (also `__ButtonLabel`, `__RoleClaim`, `__AdminValues__0` (the groups that make server admins), `__PasswordsOnlyForAdmins`). It becomes the first of the console's identity providers; more are added in **Server settings → Authentication → Sign-in method**. Register `<PublicUrl>/console/v1/session/oidc/callback` at each provider, and optionally `<PublicUrl>/console/v1/session/oidc/backchannel-logout` as its back-channel logout URL. |
| `Winche__TrustedProxies__0`, `__1`, … | Optional: the proxies whose `X-Forwarded-For`, `-Proto` and `-Host` are believed, as addresses or networks (`35.191.0.0/16`), or `*` for any. Default: loopback and private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`). Behind a proxy that connects from a public address, set it, or console sign-in fails (the log says which address the proxy came from). |
| `Winche__SecretsPrefix`, `Winche__SecretsAwsServiceUrl` | Optional: what names of secrets Winche keeps in AWS start with (default `winche`, then the project's id), and another endpoint for Secrets Manager and Parameter Store (LocalStack, a VPC endpoint). |
| `Winche__RequireTwoFactorForSettings` | Default `true`: changing settings needs two-step sign-in (a trusted provider's own second step counts). Set `false` only to try the server out. Also in **Server settings → Authentication → Sign-in method**. |
| `Winche__FunctionsMaxMinutes` | Optional: the longest any function may run, in minutes (default 9, at most 60). Also in **Server settings → General**; projects set their default and each function's own time up to it (**Functions → Settings**). |
| `Winche__FunctionsMaxMemoryMb`, `__FunctionsMaxConcurrency`, `__FunctionsSharedProcessMb`, `__FunctionsMaxProcesses` | Optional: the most memory a function may ask for (default 1024 MB), runs at once per project (default 8 per CPU, at least 8), each run's heap limit in a project's shared functions process (default 1024 MB), and functions processes per project (default 4). Also in **Server settings → General**. |
| `WINCHE_FUNCTIONS_MEMORY_MB` | Optional: what functions' processes may hold in all (default 60% of the container's memory, or the machine's). An isolated function that wouldn't fit runs in the shared process. |
| `Winche__ServiceAccountKeyMaxDays`, `__ServiceAccountKeysAllowed` | Optional: the longest a service account's key may last (default 730 days), and whether keys are allowed at all (default `true`; `false`: only trusted identities sign service accounts in). |
| `Winche__FunctionPackagesPath` | Optional, for servers that can't reach npm: a folder of the npm packages functions use (`@winche-technologies/winche-functions/`, `@winche-technologies/winche-admin/`: each its `package.json` and built files), given to deployments that import them without naming them in `deno.json`. Unset (the default), projects name their packages and the server fetches them when it bundles a deployment. |
| `Winche__FunctionPluginsPath` | Optional: a folder of plugin packages (each a folder with its `package.json`, `winche-plugin.json` and built files) the server provides instead of npm: for servers that can't reach npm, or plugins of your own. Several versions of one can sit side by side (one folder each); the newest is offered. |
| `Winche__PluginCatalog__0`, `__1`, … | Optional: the plugin packages the console offers projects (the ones you trust). Also in **Server settings → Plugins**. Projects' admins can still install others, warned first. |
| `Winche__StopTimeoutSeconds` | How long a stop (SIGTERM) waits for requests and function runs going on to end (default 30). Give the container at least as long. |
| `Winche__ProjectsPath` | Projects' files on disk, each under `<id>/files`, until they're moved to a bucket (default `/data/projects`: mount a volume). The image runs as uid **1654**: a named volume is fine as it is, but a folder mounted from the host (`-v /srv/winche:/data`) must belong to it (`chown -R 1654:1654 /srv/winche`). The server checks at start and refuses to start, saying so, when it can't write there. |
| `Winche__Email__Provider`, `__Region`, `__FromAddress` | Optional: the server's own email, `ses` (Amazon SES), e.g. region `eu-west-1`, from `no-reply@example.com` (verified in SES); `__FromName` sets the name, else each project's app name. **No keys are kept**: the AWS credentials of the server's environment are used (`AWS_*` variables, the ECS task's role, the instance's profile). Console emails go through it with nothing to set up, and projects can choose it for their sign-in emails (**Authentication → Settings → Emails to users → The server's email**). |

Several servers can share one database: settings, rules and webhooks follow within seconds, and console sign-ins
carry over (their keys are kept in the database).

- `GET /livez`: 200 while the process runs (the container's own check).
- `GET /healthz`: 200 once projects are started and warm (their functions' kept instances loaded) and the database
  answers; 503 `starting` before, and `stopping` once a stop began (for the load balancer).

### On AWS Fargate (or any container platform)

A task is one machine: the server, its projects and their functions share its CPU and memory, and past its memory the
whole task stops. Functions keep to 60% of it (`WINCHE_FUNCTIONS_MEMORY_MB` to choose); runs at once follow its vCPUs.
Run two or more tasks behind a load balancer: each runs every project, triggers and schedules run once across them,
and a deploy reaches every task within seconds.

```jsonc
// Task definition (excerpt): 2 vCPU, 4 GB
{
  "cpu": "2048", "memory": "4096",
  "containerDefinitions": [{
    "name": "winche", "image": "ghcr.io/flameofudun/winche-server:4",
    "portMappings": [{ "containerPort": 8080 }],
    "stopTimeout": 60,                                     // ≥ Winche__StopTimeoutSeconds
    "environment": [
      { "name": "Winche__PublicUrl", "value": "https://api.example.com" },
      { "name": "Winche__StopTimeoutSeconds", "value": "45" }
    ],
    "secrets": [
      { "name": "Winche__ConnectionString", "valueFrom": "arn:aws:secretsmanager:…:winche-db" },
      { "name": "Winche__MasterKey", "valueFrom": "arn:aws:secretsmanager:…:winche-master-key" }
    ],
    "healthCheck": { "command": ["CMD-SHELL", "curl -fs localhost:8080/livez || exit 1"], "interval": 15, "startPeriod": 60 }
  }]
}
```

- **Target group**: health check path `/healthz` (a new task gets traffic once warm), deregistration delay ≥ the
  stop timeout.
- **Stopping** (a deploy, scale-in, Spot): the task is taken out of the load balancer, then gets SIGTERM; it takes no
  new function runs (`unavailable`, which apps retry), lets those going on end within the stop timeout, and stops.
  Triggered runs cut off are delivered again on another task. Longer work saves its progress as it goes.
- **Scaling**: ECS service auto scaling on CPU or the load balancer's requests per target; keep a minimum of two
  tasks. A new task takes tens of seconds to be ready, so functions that must answer at once keep `minInstances`.
- **Files**: put projects' files in S3 (**Project settings → Storage**) rather than on the task's disk; bundles and
  Deno's cache live on the task's own storage and are made again on a new task.
- **Isolation**: projects on one server share the task (separate processes and workers, not separate machines).
  Tenants that must not share a machine get servers of their own.

## The console

- **Overview:** each product with its numbers, recent changes, your apps.
- **Authentication:** your apps' users, sign-in methods (email and password, email links, guests, Google, Apple, any
  OpenID Connect provider, optionally kept in step with Keycloak), trusted token issuers, email templates, webhooks,
  sessions and signing keys.
- **Database:** a data browser that refreshes as apps write, plus rules (an editor with a simulator), indexes and
  expiry (a table or text), change history, webhooks, logs and settings.
- **Storage:** files, rules, webhooks, logs and settings (size limit, a disk or an S3-compatible bucket).
- **Functions:** TypeScript functions run by Deno: callables apps call, HTTP functions, schedules, and triggers on
  documents, files, users and assets. You write them in your editor and deploy them with the CLI (`winche functions
  deploy`, or a service account). The console shows each one with what it does, runs callables, lists each trigger's runs, sets
  each function's run time, and keeps every deploy in its change history (Settings → Change history) to roll back to.
- **Plugins:** packages of functions from npm a project installs instead of writing them (`winche plugins:install`,
  or `"plugins"` in winche.json), each copy under an id with its own settings and secrets (`<ID>__<NAME>`); its
  functions are `<id>.<name>`. Installing, changing, upgrading and removing one are functions versions, in the change
  history with the code. The console API is `/console/v1/projects/<id>/functions/plugins`; the server needs to reach
  the npm registry (it already does for functions' packages). See Winche.Functions' README (Plugins).
- **Secrets:** the API keys and passwords functions read (`secrets.define`), and those the products keep (the SMTP
  password, a sync's or provider's client secret, a bucket's key), on one page. Each is a value kept encrypted and never
  shown again, or a reference read from AWS Secrets Manager or Parameter Store (`aws-ssm:/app/key`, with Test). A
  project can keep an AWS access key of its own for those (optional); without one, references use the server's AWS
  credentials and region (`AWS_REGION`, the task's role), which only server admins may choose. Where the key may write,
  Winche can also keep a secret in AWS itself (Secrets Manager, or Parameter Store as a SecureString) under
  `winche/<project>/functions.secret.<NAME>` (the products' own by their names; the server's own under `winche/_server/`):
  only the reference stays here; replacing writes a new version there, removing deletes it
  there (Secrets Manager: recoverable for 7 days), and a value kept here can be moved. The console offers only what the
  key may do, found without changing anything in AWS; the key needs, under that prefix, `secretsmanager:GetSecretValue`,
  `DescribeSecret`, `CreateSecret`, `PutSecretValue`, `RestoreSecret`, `DeleteSecret` and/or `ssm:GetParameter`,
  `DescribeParameters`, `PutParameter`, `DeleteParameter`. Winche never writes over a secret it didn't make. Deleting a
  project leaves the secrets Winche kept for it in AWS: remove them there.
- **Assets:** versioned content apps read (translations, pages, media): numbered releases published from the CLI
  (`winche assets deploy`, or a service account with the `assets` scope), channels apps follow (moving one back is a
  rollback), and rules per path. Kept in the project's files folder unless the console moves them to a bucket. Large
  files can stay on a CDN that signs links (CloudFront, **Assets → Settings → CDNs**, its private key a value or a
  reference to AWS): releases name them by address, apps never see it and get links that work for a few minutes.
  **Assets → Files** lists what apps get on each channel, with previews; admins can make a release there too (add,
  replace or take out files), and a later deploy from the repository's folder asks before taking those out.
- **Project pages:** pages of the project's own in the console's sidebar: its HTML, JS and CSS, deployed with its
  functions, shown in a sandboxed frame, reading and writing the project's data with @winche-technologies/winche-admin as the person
  who opened it (their role limits it). See Winche.Functions' README.
- **Admin API** (`/console/v1/projects/<id>/admin`): what @winche-technologies/winche-admin sends from console pages and other servers
  (`initializeAdmin` with a token), limited by the caller's role: a viewer reads, a member also writes the data, an
  admin may also call functions.
- **Service accounts** (**Project settings → Service accounts**, `winche accounts`): what CI and other servers act as,
  with a role or deploy scopes. They sign in with a key (at most two, so one replaces the other with no downtime) or a
  trusted identity: a CI system's or cloud host's own OpenID Connect token for its job, with no key to keep. Either is
  exchanged at `/console/v1/projects/<id>/service-accounts/token` for an access token that lasts an hour; the CLI
  (`WINCHE_TOKEN`, `WINCHE_ID_TOKEN`) and `@winche-technologies/winche-admin` (`initializeAdmin({ key })`) do that themselves. They belong to
  the project: the console flags one whose maker is no longer an admin, and keys unused for 90 days. Their changes are
  named for them in the history. CI tokens from before became service accounts (their tokens are their keys).
- **Integrity** (`winche integrity`): the project's apps (Android package, Apple bundle and team id; each app's id goes
  in its `winche_options.dart`) prove they're its own, trade that for an Integrity token at
  `/projects/<id>/integrity/v1/exchange`, and send it with every request (`X-Winche-Integrity`). Each product is off,
  monitor (the default: checked and counted per app, never refused) or enforce (refused without a valid token). Apps on
  iOS and macOS prove themselves with Apple's App Attest (`winche_app_attest`; the app's Apple team and bundle id, and
  whether development builds are accepted); the console shows how many devices attested. Android apps prove themselves
  with Google Play Integrity (the app's Google Cloud project number and device level; the project's Google service
  account, kept as a secret, signs the server in to Google to read each verdict). Web apps pass a browser check
  (Cloudflare Turnstile: the app's site key, sites and secret key) — weaker: it tells a browser from a script. Debug tokens (shown once)
  let simulators, desktop builds and CI in. Email action links, identity providers' notices, HTTP
  functions (webhooks) and signed file links never need a token. Functions see which app called (`ctx.app`), and a
  callable with `{ integrity: true }` answers only apps with a valid token whatever Functions' mode (Winche.Functions
  3.2). See Winche.Integrity's README.
- **Logs:** every product's requests and events, as plain sentences, with filters and saved queries. Changes are always
  logged; successful reads are sampled.
- **Project settings:** the project (name, id, image), members and invites, service accounts, and
  **Data** (where its database is, moving it, connections). The CLI signs in with `winche login`, once per server, as
  a session of your account like the console's own.
- **Server settings** (server admins): the console's own accounts. Users, sign-in method (email and password, which is
  always there, plus identity providers with role mapping and Keycloak sync), email templates, and settings (sessions,
  lockout, the SMTP server for console emails, signing keys).

Settings are saved with a reason, as versions you can restore. A save made from an older version than the latest is
refused, so two admins can't undo each other's changes.

Console emails (invites, password resets) go through the SMTP server set in **Server settings → Authentication →
Settings → Emails to users**, else the server's own email (`Winche__Email__*`). Until there is one, invites are shared as links, and a server admin sets a new password
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

**Buckets and the server's credentials.** A bucket set up without an access key is reached with the server's own AWS
credentials (its role). On a server with several projects that would give one project whatever the server can reach, so
only server admins can choose such a bucket; a project's admins give theirs an access key of its own.

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

**4.0.0 (Integrity):** registered apps became Integrity's apps (they carry over, ids and all) and "registered apps
only" and the browser origins are gone: the project APIs answer any origin, and apps no longer send `X-Winche-App`.
Every product starts in monitor: nothing is refused until you enforce it (**Integrity**). Apps built with the Winche
SDKs before 4.0 keep working while nothing is enforced; to enforce, update them and give each a way to prove itself
(App Attest on iOS and macOS, Play Integrity on Android, the browser check on the web, or a debug token). Integrity's
changes need two-step sign-in where settings do. Also new:

- **Users' photos** (Winche Auth 2.1): apps upload a small photo (`POST /auth/v1/me/photo`) kept with the user and served
  at an address of its own; sign-in providers' pictures stay links, refreshed at each sign-in. A migration adds a table
  and a column to each project's auth database when it starts. `PATCH /auth/v1/me` is now a patch: a field left out
  stays as it is (before, a missing `photoUrl` cleared the photo).
- **Editing users in the console:** a user's dialog edits their name, photo (upload or link), email (at once, or by a
  link to the new address) and password. Users from a Keycloak with sync on are changed in Keycloak (the console
  refuses here); their photo can still be uploaded here.
- **Secrets can be kept in AWS** by Winche (where the project's key, or the server's for its admins, may write), and
  values kept here moved there (Winche.Secrets 2.1: a column is added to each project's secrets table at start).
- **Forwarded headers are believed only from proxies on loopback and private networks.** **Behind a proxy that
  connects from a public address** (Google Cloud's load balancer, a CDN proxying straight to the server), set
  `Winche__TrustedProxies__0` to its network (`*`: any, as before) **before upgrading**, or console sign-in fails. A
  server reached directly can't be told another caller's address or host.
- **The console refuses changes another site's page makes** with its cookie (a browser's `Sec-Fetch-Site` or `Origin`
  says so). The console's own pages, the CLI and service accounts aren't affected.

**3.1.4:** with an SMTP server, sign-in-link and password-reset emails are sent again (they were lost, "Cannot access a
disposed object" in the log; Winche Auth 2.0.1). Authentication → Settings → Emails to users can now send them before
answering instead. Nothing to change.

**3.1.3:** plugins from a folder (`Winche__FunctionPluginsPath`) build with the newest SDKs, like plugins from npm
(Winche.Functions 3.1.2). Nothing to change.

**3.1.2:** a project whose creation fails part-way is undone (a retry works); the server checks at start that it can
write to its data folder; file errors are said plainly in the console and CLI. Nothing to change, but a host folder
mounted on `/data` must belong to uid 1654.

**3.1.1:** plugins from npm install on a server whose npm cache is older than the SDK they need (Winche.Functions
3.1.1). Nothing to change.

**3.1:** nothing to change; new:

- **Plugins** (sidebar → Plugins): packages of functions from npm that projects install, configure, upgrade and remove
  from the console (a page per package, a step-by-step install, a page per installed copy with its setup steps), the
  CLI (`winche plugins:*`) or winche.json. Server settings → Plugins (`Winche__PluginCatalog__0`, …) lists the ones the
  server offers; `Winche__FunctionPluginsPath` provides plugin folders instead of npm. Functions' `ctx.byServer`.
- **Assets:** files kept on a CDN (CloudFront signed links, or a public CDN), Assets → Files with the live files and
  previews, and releases made in the console.
- Functions 3.1 (Winche.Functions), Assets 1.1 (Winche.Assets).

**3.0:** one layout for every address (a clean break: the old addresses don't answer).

- Apps: `/p/<id>/…` became `/projects/<id>/…`. Update apps' base URL (`…/projects/<id>`) and register the new callback
  URLs at the sign-in providers apps use; links sent before (file links, email action links) stop working, and app users
  sign in again.
- The console: `/_console` and each project's `/p/<id>/_console` became one console at `/console` (a project's pages at
  `/console/projects/<id>`), with its API at `/console/v1/…` (a project's at `/console/v1/projects/<id>/…`), named as
  the products are (`documents`, `files`, `auth`). Console sign-in through a provider: register
  `<PublicUrl>/console/v1/session/oidc/callback`. Everyone signs in to the console again; CLI 3.0 is needed.
- Files kept on the server's disk are served from `/projects/<id>/files/v1/signed/…` (was `/p/<id>/_files/…`).
- `Winche__ConsolePath` is gone.

**2.2:** function instances: each function may set its memory, instances kept loaded, most at once, reserved runs,
and a process of its own (in code or in its **Settings** tab; nothing changes for functions that don't), with an
**Instances** tab to watch them. The server's limits are in **Server settings → General**. `/healthz` now answers 503
until the server is warm and while it stops; point container health checks at the new `/livez`. A stop waits for
function runs going on (`Winche__StopTimeoutSeconds`, default 30).

**2.1:** functions answer in a couple of milliseconds: runs reuse warm workers (Winche.Functions 2.1; a module's
top-level state lasts between runs, so it must never hold one caller's data), and the server warms each project up
when it starts.

**From 1.x to 2.0:** a 2.x server has no project at its root: every project is under `/p/<id>`, and the server runs
with none until one is added. 2.0 doesn't carry a 1.x server's root project over, so set 2.0 up on new databases.

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
