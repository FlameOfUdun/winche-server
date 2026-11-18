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

Products: `documents`, `files`, `auth` (the app's users), `assets`, `functions.call`. The API is the one `ctx` has
today.

### Functions: data from the SDK, `request` for the call

A function's second argument stops being `ctx`. It describes the call only, and data comes from the SDK, already signed
in as the server inside functions:

```ts
import { documents } from "@winche/admin";
import { callable, secret } from "@winche/functions";

export const hello = callable(async (data, request) => {
  request.log("by", request.caller?.uid);
  return (await documents.doc(`profiles/${request.caller!.uid}`).get()).data();
});
```

- **`request`** (callables, HTTP functions, console actions): `caller` (the app user), `console.user` (console
  actions), `log`. Triggers (documents, files, schedules) get **`event`** instead: what happened, its `params`, `log`.
- **Secrets and resources** belong to the deployment, not to the call or the SDK (pages and other servers never read
  secrets): `secret("NAME")` and `resource("fonts/a.ttf")` from `@winche/functions`.
- **A clean break:** `ctx.documents`, `ctx.files`, `ctx.auth`, `ctx.assets`, `ctx.secrets`, `ctx.resources` are
  removed; a deployment that uses them is refused with what to import instead.

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

A function called by an app user can choose to act as that user: `admin.as(request.caller)` (or
`admin.as({ uid, claims })`) is the same SDK, but every documents and files call is checked against the project's rules for that user, exactly as
if the app had made it. The plain SDK stays available beside it:

```ts
import * as admin from "@winche/admin";

export const leaveWard = callable(async (data, request) => {
  const asUser = admin.as(request.caller);
  await asUser.documents.doc(`wards/${data.ward}/members/${request.caller.uid}`).delete();   // the rules decide
  await admin.documents.collection("audit").add({ who: request.caller.uid, left: data.ward }); // the function's own record
});
```

The database's and storage's rules engines evaluate these calls with the caller's identity; a refusal is
`permission-denied`, as an app gets.

### Console pages

A page stays a plain sandboxed frame without the console's sign-in. The SDK in a page sends each call to the console
over the page's channel (`window.winche`); the console runs it on the project's console API as the signed-in person.

- The page's actions (`consoleAction`) stay, for work that needs the server's rights (long jobs, other users' data
  beyond the person's role).
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
- **Pages' calls:** the console relays a page's calls to that API (not only `pages/actions/{name}`), with the person's
  session; the page never holds it.
- **Acting as a user:** the functions runtime passes `as(user)` calls with that identity; the database and storage
  evaluate their rules for it.
- **Functions:** the context becomes `request` (or `event`); its data members are removed (the deploy refuses code that
  uses them, naming the import to use).

## Steps

1. **The SDK package** (`@winche/admin`): the API from `ctx` today, one transport interface; in functions, signed in as
   the server. `request` and `event` replace `ctx`; `secret()` and `resource()` imports. Tests: the same suite run
   against each transport.
2. **The console's project API** fills the SDK's gaps (transactions, batches, cursors, uploads).
3. **Pages:** the SDK's page transport over the console's channel; the console relays calls as the person.
   `@winche/console-page` and the pages template removed.
4. **Other servers:** the token transport (with `docs/service-accounts.md`'s accounts).
5. **`admin.as(user)`:** the rules applied to a function's calls for a given user.
6. **Docs and release:** functions SDK (a major version: `ctx` is gone), CLI (pages), console, server. Upgrading notes:
   each `ctx.x` and its import.

## Open questions

- Live updates (listening to a document or query) in pages and other servers: the console would relay the database's
  socket. Later.
- Should a page's SDK calls appear in the project's history as "via page <name>"?
