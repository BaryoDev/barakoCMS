# barako CLI, first slice: posts and pages for agents

Status: draft for approval, 2026-11-18. Epic #801, generic verbs #892, config as code #345, MCP #582.

## Intent

Agents need to write and edit posts and pages on a barakoCMS site without the admin. Today the only
non-admin path is a script per content type (the hourly docs push on barakocms.com). The `barako` CLI
gives an agent one tool for any content type: read the type's shape, write a draft, check it, publish
it as a separate step, and take it back out without losing it. The shape follows how the Umbraco MCP
works (schema, draft, validate, publish, recycle bin), mapped onto what barakoCMS already has.

People can use it too, but the first reader of every output is an agent.

## Goals

- An agent can create, edit, publish, unpublish, schedule, archive and restore a post or a page, and
  read its history, with commands that work on any content type.
- Every write can be checked first with `--dry-run`, and the check is the server's own validation.
- An edit never overwrites a change the agent did not see.
- An agent's key needs content rights only, never admin or content type management.
- Output is JSON with a stable shape, and failures have a fixed exit code an agent can branch on.

## Non-goals (this slice)

- Editing content types or fields.
- Files and media upload. Posts that need an image reference an existing file id for now.
- Users, roles, API keys, settings.
- `barako apply` from a file (#345) and deploy (#801 later stages).
- The MCP server (#582). The design leaves room for it (see "Commands as data") but does not build it.

## Anti-goals

- The CLI re-implementing validation. The server decides what is valid. The CLI only shapes requests.
- One command per admin screen or per content type (the plan #892 replaced).
- An agent permanently deleting content by default.
- API keys on the command line, in shell history, or in the output.
- Output whose shape changes without a version bump.

## Constraints

- Public repositories: the CLI and the client are MIT on GitHub, the server change is in public core.
  Nothing in them names a private product or customer.
- Node 22.23.4 or newer.
- No runtime dependencies beyond `@baryodev/barako-client` (argument parsing with `node:util`
  `parseArgs`).
- The server change is additive: no existing route, field or status code changes.

## Approaches

Three were considered. Arnel picked A on 2026-11-18.

- **A. TypeScript CLI on `barako-client` (picked).** Agents run `npx barako ...` and read JSON. Each
  command is a small described function, so an MCP mode can later serve the same commands as tools.
  Reuses the client Brew and Press already use.
- **B. MCP server first, CLI later.** Closest to Umbraco, and agents get typed tools. Each agent has to
  be configured to connect, it is not scriptable from cron or CI, and the request was a CLI. A covers B
  later from the same code.
- **C. .NET global tool in core.** Same language as the server, but a second client to keep in step
  with the TypeScript one.

## What the server already has

Posts and pages are ordinary entries of the `post` and `page` types (blog blueprint). The Pages module
adds a tree over `page`. All routes below exist on master (fad8838).

| Step | Route |
| --- | --- |
| List, search | `GET /api/contents?contentType=&status=&search=` |
| Get | `GET /api/contents/{id}` (returns `version`, and an `ETag` header for document types only, not event-sourced ones), `GET /api/contents/by-slug/{type}/{slug}` |
| Create | `POST /api/contents`, status defaults to Draft, takes `dryRun` |
| Update | `PUT /api/contents/{id}`, replaces the whole `data`, takes `version` (0 skips the check, a mismatch is 412, or 409 for event-sourced types), `If-Match` (optional unless `Content:Concurrency:Require` is on) and `dryRun` |
| Publish, unpublish, archive, restore | `PUT /api/contents/{id}/status` with `newStatus` 1, 0, 2, 0. Types with a lifecycle take `transition` instead |
| Schedule | `PUT /api/contents/{id}/schedule` |
| History, rollback | `GET /api/contents/{id}/history`, `POST /api/contents/{id}/rollback/{versionId}` (needs the `content:destructive` key scope) |
| Erase | `DELETE /api/contents/{id}/erase` (irreversible, `content:destructive`) |
| Block types | `GET /api/blocks/schema` |
| Page tree | `GET /api/pages/tree` |

Gaps against the Umbraco shape, and how this design closes them:

- **Reading a type's shape needs admin.** `GET /api/content-types` requires `manage_content_types`
  and the `contenttype:read` key scope. Closed by the one server change below.
- **No partial update.** The CLI reads, merges and writes, with the version it read (see Updates).
- **No recycle bin.** Archive is the reversible removal and restore moves an entry back to Draft.
  Erase stays behind an explicit flag.
- **No dry run on status changes.** `--dry-run` on those reports the planned change from a read, and
  says the client checked it, not the server.
- **API keys cannot read the page tree or the block schema.** `ApiKeyScopeProcessor.RequiredScope`
  maps only `/api/contents`, `/api/content-types`, `/api/schemas`, `/api/files` and the push route.
  Every other path answers 403 to a key unless the endpoint names a capability. Closed in the server
  change below.
- **Conflict errors carry no current version.** The 412 and 409 bodies are plain messages. The CLI
  reads the entry again on a conflict to report the current version.

## The server change (core)

A read-only route for one content type's shape, available to anyone who may read entries of that
type:

`GET /api/contents/types/{name}` (exact path is the builder's call, see below)

- Authorization: the caller's content `read` permission on `{name}`, the same check `GET
  /api/contents?contentType={name}` makes. A key needs `content:read`, which it gets because the path
  is under `/api/contents`. No `manage_content_types`.
- Response: the type's name, display name, fields (name, type, required, validation rules, options,
  multiple, item type, reference type), its lifecycle states and transitions when it has one, and its
  uniqueness rules. Block field definitions point at the block schema rather than repeating it.
- A sibling `GET /api/contents/types` lists the names of the types the caller may read, so
  `barako types` works without admin.
- 404 for a type that does not exist or that the caller may not read, so the route does not reveal
  types to someone who cannot see them.
- The key scope checks in `ApiKeyScopeProcessor` match paths by segment shape (`IsDestructive`,
  `IsShareLinks`). `/api/contents/types/{name}` has the same segment count as
  `/api/contents/{id}/erase`, so a type named `erase` or `share-links` would be misread. The new
  route gets an explicit exclusion, as `by-slug` has, with a test for both names.
- `GET /api/pages/tree` and `GET /api/blocks/schema` map to `content:read` for keys (reads only, writes
  stay unmapped). The page tree applies the caller's read permission per entry, so a key sees only the
  pages its role may read. If it does not today, the change adds that filter.
- Additive. Core minor version.

## The CLI

Package `@baryodev/barako`, bin `barako`. It lives in the `barako-client` repository as a second
workspace package (`cli/`), versioned and published on its own, so the client stays free of CLI code.
The repository becomes an npm workspace for this. The CLI's `engines` is Node 22.23.4 or newer; the
client keeps its own, wider range.

### Connection

- `BARAKO_URL` and `BARAKO_API_KEY` from the environment, or `--key-file <path>` for the key.
  `--url` is allowed, a key on the command line is not.
- `BARAKO_TENANT` or `--tenant` for multi-tenant instances.
- The key never appears in output or errors.

### Commands

```
barako types
barako schema <type>
barako content list <type> [--status draft|published|archived|scheduled] [--search <q>] [--limit <n>] [--all]
barako content get <id>
barako content get --slug <type>/<slug>
barako content create <type> (--file <json> | --set ...) [--publish] [--dry-run] [--idempotency-key <k>]
barako content update <id> (--file <json> | --set ...) [--replace] [--expect-version <n>] [--dry-run]
barako content publish|unpublish|archive|restore <id> [--dry-run]
barako content transition <id> <name>
barako content schedule <id> --publish-at <iso> [--unpublish-at <iso>]
barako content history <id>
barako content rollback <id> <versionId> --confirm <id>
barako content erase <id> --confirm <id>
barako pages tree
barako commands
```

Field values:

- `--set Title="Hello"` sets a string.
- `--set Featured:=true` and `--set Tags:='["a","b"]'` set JSON.
- `--set-file Body=post.md` reads a field's value from a file, for markdown bodies.
- `--file entry.json` gives the whole `data` object.

### Updates

`update` with `--set` reads the entry, replaces the named top-level fields, and writes the whole
`data` back. With `--file`, the file's fields are merged the same way, and `--replace` sends the file
as the whole `data`.

The version an agent sees and passes is the numeric `version` from `get`, which every type has. The
write sends that number as `version`, and for document types also the `ETag` from the same read as
`If-Match`. The CLI never sends `version: 0`, so the check always runs even where the server does not
require `If-Match`.

An agent usually reads an entry, thinks, then writes. `--expect-version <n>` pins the write to the
version the agent read. The CLI compares it with its own fresh read first and stops with exit 3
without writing when they differ, then sends `n` as `version`. The README and agent guide tell agents
to pass it.

On a conflict (409, 412, 428) the CLI reads the entry once more and puts the current version in the
error, so the agent can read again.

### Output

- Success: one JSON object on stdout. `{ "ok": true, "result": ... }`, with `dryRun: true` and the
  server's `changes` list when it was a dry run. Entries keep the server's field names.
- Failure: one JSON object on stderr, `{ "ok": false, "error": { "code", "status", "message",
  "details" } }`. `code` is fixed per case: `usage`, `validation` (400), `rule` (422), `conflict`
  (409, 412), `version_required` (428), `not_found`, `unauthorized` (401), `forbidden` (403),
  `server` (5xx), `network`. `details` is the server's response body as given (parsed JSON, or the
  text), so the envelope is stable even where the server's own error shapes differ. The build
  captures one real sample of each server error into the tests.
- `publish`, `unpublish` and `restore` on a type with a lifecycle get the server's 400, exit 2, with a
  message pointing at `transition`. `barako schema` shows the lifecycle, so an agent can tell first.
- `-o text` prints a short human form instead. JSON is the default whether or not stdout is a
  terminal, so an agent never has to ask for it.
- Exit codes: 0 ok, 1 usage, 2 rejected by validation (400, 422), 3 conflict (409, 412, 428),
  4 not found, 5 not allowed (401, 403), 6 server or network.
- The output shape is part of the package's public surface and changes only with a major version.

### Safety

- Creates are drafts unless `--publish` is passed.
- Archive is the documented way to take something down, and restore undoes it.
- `rollback` and `erase` need `--confirm <id>` repeating the entry id, and a key with
  `content:destructive`. An agent key without that scope cannot run them at all.
- `--idempotency-key` on create is sent as the `Idempotency-Key` header, so an agent that retries a
  create after a timeout does not make two entries.

### Commands as data

Each command is defined once as `{ name, summary, args, run(client, args) }`, with `args` described
in a JSON-Schema-like form. `barako commands` prints that table as JSON so an agent can discover the
CLI without reading docs. The future MCP mode (#582) serves the same table as tools, with no second
definition.

## Client changes (`barako-client`)

Added to `@baryodev/barako-client`, each with tests:

- `dryRun` on `contents.create` and `contents.update`, returning the server's `changes`.
- `If-Match` on update, and the version and ETag from get. The transport returns only the parsed
  body today, so it gains a way to return response headers too. The existing methods keep their
  return types; `get` and `update` add an `etag` field. Additive, client minor version.
- `search` on `contents.list`.
- `contents.schedule`, `contents.rollback`, `contents.erase`, `contents.bySlug` (authoring read).
- `contentTypes.readable()` and `contentTypes.get(name)` on the new server routes.
- `blocks.schema()` and `pages.tree()`.
- `Idempotency-Key` on create.

## Tests

- Server: endpoint tests for the new routes, covering a reader, a caller without read on the type
  (404), an API key with only `content:read`, a lifecycle type, types named `erase` and
  `share-links`, and a content-only key reading the page tree (only the pages its role may read) and
  the block schema. An idempotent create repeated with the same key makes one entry, and a dry run
  followed by a real create with the same key makes one entry.
- Client: unit tests with the existing fake transport for each new call.
- CLI: unit tests for argument parsing, `--set` parsing, the merge, exit codes and output shape, and
  one integration suite that runs `barako` against a real barakoCMS container with the blog blueprint:
  create a draft post, dry-run an invalid update (exit 2, nothing saved), update with a stale
  `--expect-version` (exit 3), publish, archive, restore, and read the history.

## Rollout

1. Core: the schema routes. Minor release.
2. Client: the additions above. Minor release.
3. CLI: `@baryodev/barako` 0.1.0 with the commands above, the README, and a short agent guide
   (which commands to run in which order for "write a post").
4. On barakocms.com: a service key with `content:read` and `content:write`, and a role that may create
   and update `post` and `page`. Minting it and storing it are Arnel's.

## Decided

- Approach A, TypeScript on `barako-client` (Arnel, 2026-11-18).
- Agents are the main users. Posts and pages first, through verbs that work on any type (Arnel).
- Modelled on the Umbraco MCP flow (Arnel).

## Builder's calls (made in this doc, open to correction)

- The CLI as a separate workspace package in `barako-client`, not a bin inside the client package.
- JSON output by default, the exit code table, `--set` syntax, `--confirm <id>` for destructive
  commands.
- Archive and restore as the recycle bin.
- `--dry-run` on status commands checked by the client and labelled so.
- The schema route under `/api/contents` so the existing `content:read` scope covers it.
- The page tree and block schema reads mapped to `content:read` for keys, so the agent key stays
  content-only.
- The user-facing version is the numeric `version`; the ETag is sent too but never shown as the
  version.
- The CLI reads again on a conflict to report the current version, instead of changing the server's
  error bodies.
- A transport that can return headers, added without changing existing client signatures.
- Fixed error codes with the server body passed through as `details`.

## Left to the builder

- The exact schema route path. `/api/contents/{id}` must not swallow `types`; check the route
  constraint on `{id}`, and pick another prefix under `/api/contents` if it would.
- Paging details for `list --all`.
- The text output format.
