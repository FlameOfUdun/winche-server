# Plan: one admin SDK for functions, console pages and other servers

**Status:** plan. Builds on Server 3.0's paths (`docs/paths.md`). How other servers get their access tokens is its own
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
- **Secrets and resources come from `@winche/functions`:** `secrets.get("NAME")` / `secrets.has`, and
  `resources.read(path)` / `list` / `has`. They resolve for the function that's running (the runtime tracks it across
  async calls), so a function still sees only the secrets it declared and use is tracked per function; outside a run
  (a module's top level) they throw. Tests use `@winche/functions/testing`: `withSecrets({ NAME: "x" }, fn)`,
  `withResources(folder, fn)`. The admin SDK never has them, so pages and other servers can't read secrets.

```ts
import { callable, secrets, resources } from "@winche/functions";

export const send = callable(async (data, ctx) => {
  const key = secrets.get("MAIL_KEY");
  const logo = await resources.read("logo.png");
}, { secrets: ["MAIL_KEY"] });
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
   the server. `ctx` keeps the call's facts (`functionName` → `name`); `secrets` and `resources` imported from `@winche/functions`, with test helpers. The trigger builders renamed (`onDocument`, `onFile`,
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
