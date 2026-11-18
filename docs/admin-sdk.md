# Plan: one admin SDK for functions, console pages and other servers

**Status:** done locally (2026-11-18), unreleased: Functions 3.0.0 (with the `@winche/admin` npm package in
`npm/admin`), Storage 11.1.0, Server and CLI 3.0, the console web app. Builds on Server 3.0's paths (`docs/paths.md`). How other servers get their access tokens is its own
plan (`docs/service-accounts.md`); this one only needs "a token".

## Why

Today a project's data is reached three different ways:

- **Functions** have `ctx.documents`, `ctx.files`, `ctx.auth`, `ctx.assets` on their context, with the server's rights.
- **Console pages** have none of that: every read goes through an action written for it (`consoleAction`), even a plain
  list of documents.
- **Other servers** have nothing: CI tokens can only deploy.

A function, a page and a server that do the same thing are written three ways.

## What it is

`@winche/admin`: one plain TypeScript SDK (no framework, no UI) for a project's data. It is the same API wherever it
runs; only how it signs in differs.

```ts
const db = admin.documents;
const ward = await db.doc("wards/a").get();
await admin.files.upload("reports/a.pdf", bytes);
const user = await admin.auth.getUser(uid);
await admin.functions.call("recompute", { ward: "a" });
```

| Where | Signs in as | Limited by |
| --- | --- | --- |
| Functions | the server | nothing (full rights) |
| Console pages | the person who opened the page | their role in the project (viewer, member, admin) |
| Other servers | a service account's access token | the account's role |

Products: `documents`, `files`, `auth` (the app's users), `assets`, `functions.call` (not in pages: callables are for
app users). The API is the one `ctx` has
today.

### Functions: data from the SDK, `ctx` for the call

A function's `ctx` (its second argument everywhere: callables, HTTP functions, triggers, schedules)
describes the call only; data comes from the SDK, already signed in as the server inside functions:

```ts
import { documents } from "@winche/admin";
import { callable } from "@winche/functions";

export const hello = callable(async (data, ctx) => {
  ctx.log("by", ctx.caller?.uid);
  return (await documents.doc(`profiles/${ctx.caller!.uid}`).get()).data();
});
```

- **`ctx`:** the call only: `name` (the function's export name; today's `functionName`), `caller` (the app user, for
  callables), `log`. Triggers keep their event as the first argument.
- **Secrets are declared at the top, given to the functions that use them:** `const mailKey = secrets.define("MAIL_KEY")`
  (from `@winche/functions`) is a handle; a function lists the handles it uses (`{ secrets: [mailKey] }`) and reads
  `mailKey.value()` while it runs, getting the current value. Reading one the running function didn't list throws,
  naming both. The deploy checks every listed secret is set on the project. The admin SDK never has secrets, so pages
  and other servers can't read them.
- **Resources likewise:** `const logo = resources.define("logo.png")`, then `await logo.bytes()` / `logo.text()` while a
  function runs (any function may read the deployment's resources).
- **Tests** (`@winche/functions/testing`): `withSecrets({ MAIL_KEY: "x" }, fn)`, `withResources(folder, fn)`.

```ts
import { callable, secrets, resources } from "@winche/functions";

const mailKey = secrets.define("MAIL_KEY");
const logo = resources.define("logo.png");

export const send = callable(async (data, ctx) => {
  await sendMail(mailKey.value(), await logo.bytes(), data);
}, { secrets: [mailKey] });
```
- **A clean break:** `ctx.documents`, `ctx.files`, `ctx.auth`, `ctx.assets` are removed; a deployment that uses them is
  refused with what to import instead.

### Roles (pages and other servers)

The console's project API already checks roles per call; the SDK uses it as it is:

| Role | May |
| --- | --- |
| viewer | read |
| member | read and write data (documents, files, the app's users) |
| admin | everything the project's console API offers |

Nothing else limits a page: what it shows, asks or does with what it reads is its author's responsibility.

### Rules: not by default, by choice

Access rules protect the apps' paths. The SDK, signed in as the server, an account or a console person, doesn't apply
them (roles do instead).

A function called by an app user can choose to act as that user: `admin.as(ctx.caller)` (or
`admin.as({ uid, claims })`) is the same SDK, but every documents and files call is checked against the project's rules for that user, exactly as
if the app had made it. The plain SDK stays available beside it:

```ts
import * as admin from "@winche/admin";

export const leaveWard = callable(async (data, ctx) => {
  const asUser = admin.as(ctx.caller);
  await asUser.documents.doc(`wards/${data.ward}/members/${ctx.caller.uid}`).delete();   // the rules decide
  await admin.documents.collection("audit").add({ who: ctx.caller.uid, left: data.ward }); // the function's own record
});
```

The database's and storage's rules engines evaluate these calls with the caller's identity; a refusal is
`permission-denied`, as an app gets.

### Trigger names

`@winche/admin` exports `documents`, `files` and `assets` (data), so the trigger builders in `@winche/functions` are
renamed to read as events and never clash with them:

| Today | Renamed |
| --- | --- |
| `documents.onCreated / onUpdated / onDeleted / onWritten / beforeWrite` | `onDocument.created / updated / deleted / written / willBeWritten` |
| `files.onUploaded / onDeleted / onMetadataUpdated / beforeWrite` | `onFile.uploaded / deleted / metadataUpdated / willBeWritten` |
| `users.onCreated / onDeleted / onSignedIn / onDisabled / beforeCreate / beforeSignIn` | `onUser.created / deleted / signedIn / disabled / willBeCreated / willSignIn` |
| `assets.onReleased / onChannelMoved` | `onAssets.released / channelMoved` |

Events that already happened are in the past tense; the ones that can still change or refuse what's about to happen
say `will…`. `callable`, `http.onRequest`, `schedule` and `pages` keep their names. Part of the same clean break: the deploy refuses
the old names, naming the new one.

### Console pages

A page stays a plain sandboxed frame without the console's sign-in. The SDK in a page sends each call to the console
over the page's channel (`window.winche`); the console runs it on the project's console API as the signed-in person.

- **No console actions:** `consoleAction` and `ctx.console` are removed. Work that needs the server's rights (long
  jobs) is asked for with a document the page writes (e.g. `jobs/backfill` with `status: "requested"`); a document
  trigger runs it and writes its progress there, and the page reads it. Who may start it is whoever may write there.
- **Declaring a page:** `pages({ title, files, icon, minRole, order })` from `@winche/functions` (today's
  `consolePage`).
- The console gives a page nothing else: no dialogs, notices or titles. `@winche/console-page` (with its React hooks)
  and `winche pages init`'s template come out. The CLI keeps building pages that name a build in `winche.json`
  (`pages`).

### Other servers

```ts
const admin = initializeAdmin({ server: "https://winche.example.com", project: "clinic", credentials: /* see service-accounts.md */ });
```

The SDK gets and refreshes its access token; calls go to `/console/v1/projects/<id>/…` with it.

## As built

- **One admin API** (`POST /console/v1/projects/<id>/admin`, `{ op, args }`) runs the same operations functions send
  (the bindings), so every place behaves the same; the role needed comes from the operation (reads, data writes,
  the rest). A page's calls carry `X-Winche-Page` and may not call functions.
- **Pages** get `@winche/admin` from an import map the console adds (`_winche/admin.js`, the SDK bundled for
  browsers by the server's Deno) and a bridge script that carries its calls to the console.
- **Functions** keep their run's state across awaits (`AsyncLocalStorage`): `@winche/admin`, secrets and resources
  find the run they belong to; at a module's top level they throw.
- **Other servers**: `initializeAdmin({ server, project, token })` over HTTPS; tokens are today's console tokens until
  service accounts (`docs/service-accounts.md`).

## Server changes

- **One API for the SDK:** the console's project API (`documents`, `files`, `auth`, `assets`) covers what the SDK
  needs: batched writes and transactions, queries with cursors, file upload and download, the app's users. Gaps are
  added there, so the console's own screens get them too.
- **Pages' calls:** the console relays a page's calls to that API (`pages/actions/{name}` is removed), with the person's
  session; the page never holds it.
- **Acting as a user:** the functions runtime passes `as(user)` calls with that identity; the database and storage
  evaluate their rules for it.
- **Functions:** `ctx` loses its data members (the deploy refuses code that uses them, naming the import to use);
  `functionName` becomes `name`.

## Steps

1. **The SDK package** (`@winche/admin`): the API from `ctx` today, one transport interface; in functions, signed in as
   the server. `ctx` keeps the call's facts (`functionName` → `name`); `secrets.define(...)` / `resources.define(...)` handles from `@winche/functions` (functions list their secrets' handles), with test helpers. The trigger builders renamed (`onDocument`, `onFile`,
   `onUser`, `onAssets`). Tests: the same suite run
   against each transport.
2. **The console's project API** fills the SDK's gaps (transactions, batches, cursors, uploads).
3. **Pages:** the SDK's page transport over the console's channel; the console relays calls as the person.
   `@winche/console-page` and the pages template removed.
4. **Other servers:** the token transport (with `docs/service-accounts.md`'s accounts).
5. **`admin.as(user)`:** the rules applied to a function's calls for a given user.
6. **Docs and release:** functions SDK (a major version: data leaves `ctx`), CLI (pages), console, server. Upgrading notes:
   each `ctx.x` and its import.

## Open questions

- Live updates (listening to a document or query) in pages and other servers: the console would relay the database's
  socket. Later.
- Should a page's SDK calls appear in the project's history as "via page <name>"?
