# barako CLI first slice, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An agent can read a content type's shape and create, edit, publish, schedule, archive and restore posts and pages with `npx barako`, using a key that has content scopes only.

**Architecture:** Three parts, shipped in order. Core gets two read routes for type shapes and maps the page tree and block schema reads to `content:read` for keys. `@baryodev/barako-client` gets the calls the CLI needs, including response headers for the ETag. A new package `@baryodev/barako` in the barako-client repo defines each command once as data, runs it against the client, and prints one JSON envelope with a fixed exit code.

**Tech Stack:** ASP.NET Core 10, FastEndpoints, Marten, xUnit with FluentAssertions (core). TypeScript, tsup, vitest, `node:util` `parseArgs` (client and CLI).

**Spec:** `docs/internal/design/2026-11-18-barako-cli-content.md` (same folder). Read it before any task.

## Global Constraints

- Public repositories. No private product or customer name in code, tests, commits, branches or PR text.
- No em dashes, no arrow glyphs, no attribution trailers in any commit, PR body, doc or comment.
- Core change is additive: no existing route, field or status code changes.
- CLI: Node 22.23.4 or newer (`engines`). Its only runtime dependency is `@baryodev/barako-client`. Client keeps `engines` `>=18` and zero runtime dependencies.
- The API key is never read from a command-line argument and never appears in stdout or stderr.
- Output shape: success `{ "ok": true, "result": ... }` on stdout; failure `{ "ok": false, "error": { "code", "status", "message", "details" } }` on stderr.
- Exit codes: 0 ok, 1 usage, 2 validation or rule (400, 422), 3 conflict (409, 412, 428), 4 not found, 5 not allowed (401, 403), 6 server or network.
- Error codes: `usage`, `validation` (400), `rule` (422), `conflict` (409, 412), `version_required` (428), `not_found`, `unauthorized` (401), `forbidden` (403), `server` (5xx), `network`.
- Implementers build only. The coordinator queues tests (`brain queue add <worktree> <project> --filter <class or file>`, then the drainer) and makes the commits. For the npm repos the project argument is `package.json`; if the drainer has no npm runner, a `lean:gate` agent runs `npm test -- <file>` and pastes the output.
- Worktrees: core `~/lean-wt/core-801-cli` on branch `core/801-cli-schema-routes`, branched from `docs/barako-cli-design` so the design and this plan ride in the same PR. Client and CLI `~/lean-wt/client-cli` on `feat/authoring-calls`, then `feat/barako-cli` stacked on it.

**Spec correction found while planning:** the server derives the tenant from an API key, and the client ignores `tenant` for keys (`src/client.ts`, `ClientConfig.tenant`). The CLI drops `BARAKO_TENANT` and `--tenant`. The spec's Connection section is updated in Task 6.

## Review Focus

1. `barako content restore` on an entry that is not archived would quietly turn a published post into a draft. It must refuse (exit 1, `usage`) and say to use `unpublish`. Test in Task 9.
2. A role whose read rule has a condition (for example own entries only) must still get the type's shape from `GET /api/contents/types/{name}`, since it can read some entries. Test in Task 2.
3. The key leaking through an error: a network error message or a server body that echoes the request. Every output string is scrubbed of the key. Test in Task 6.
4. `--expect-version` on an event-sourced type: there is no ETag, so no `If-Match` is sent, the server answers 409, and the CLI exits 3 with the current version. Test in Task 9.
5. `--set` values with `=` in them (`Title=a=b`) and empty values (`Title=`): split on the first `=`, an empty string is a valid value. Test in Task 7.

---

## Part 1: core (`~/lean-wt/core-801-cli`)

### Task 1: key scopes for the page tree, the block schema and the types route

**Files:**
- Modify: `barakoCMS/Infrastructure/Auth/ApiKeyScopeProcessor.cs` (`RequiredScope`, `IsDestructive`, `IsShareLinks`, `IsReadOnlyReadable`)
- Test: `BarakoCMS.Tests/ApiKeyScopeRoutingTests.cs`

**Interfaces:**
- Produces: `RequiredScopes("/api/pages/tree", false)` and `RequiredScopes("/api/blocks/schema", false)` equal `["content:read"]`; with `isWrite: true` both stay null. `/api/contents/types` and `/api/contents/types/{name}` map like any `/api/contents` read, whatever `{name}` is.

- [ ] **Step 1: Write the failing tests** in `ApiKeyScopeRoutingTests`:

```csharp
[Theory]
[InlineData("/api/pages/tree")]
[InlineData("/API/Pages/Tree/")]
[InlineData("/api/blocks/schema")]
public void Authoring_reads_need_content_read(string path) =>
    ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().Equal("content:read");

[Theory]
[InlineData("/api/pages/tree")]
[InlineData("/api/blocks/schema")]
public void Authoring_read_paths_stay_closed_to_key_writes(string path) =>
    ApiKeyScopeProcessor.RequiredScopes(path, isWrite: true).Should().BeNull();

[Theory]
[InlineData("/api/contents/types")]
[InlineData("/api/contents/types/post")]
[InlineData("/api/contents/types/erase")]
[InlineData("/api/contents/types/share-links")]
[InlineData("/api/contents/types/rollback")]
public void The_types_route_is_a_plain_content_read(string path) =>
    ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().Equal("content:read");

[Theory]
[InlineData("/api/pages/tree")]
[InlineData("/api/blocks/schema")]
[InlineData("/api/contents/types/post")]
public void A_read_only_key_may_read_the_authoring_reads(string path) =>
    ApiKeyScopeProcessor.IsReadOnlyReadable(path).Should().BeTrue();
```

- [ ] **Step 2: Queue the class and confirm it fails**

Run: `brain queue add ~/lean-wt/core-801-cli BarakoCMS.Tests/BarakoCMS.Tests.csproj --filter ApiKeyScopeRoutingTests`, then the drainer.
Expected: the tree, block schema, `erase` and `share-links` cases fail; the rest pass already.

- [ ] **Step 3: Implement.** In `RequiredScope`, before the final `return null`, return `ContentRead` for a GET of exactly `/api/pages/tree` or `/api/blocks/schema` (use the existing `Match` and trailing-slash handling). In `IsDestructive` and `IsShareLinks`, exclude `segments[2] == "types"` (case-insensitive) the way `by-slug` is excluded, and update both comments to name it. Add the three paths to `IsReadOnlyReadable`.

- [ ] **Step 4: Queue the class again.** Expected: all pass.

- [ ] **Step 5: Commit** `map the page tree and block schema reads to content:read for keys`

### Task 2: `GET /api/contents/types` and `GET /api/contents/types/{name}`

**Files:**
- Create: `barakoCMS/Features/Content/Types/Models.cs`, `barakoCMS/Features/Content/Types/ListEndpoint.cs`, `barakoCMS/Features/Content/Types/GetEndpoint.cs`
- Test: `BarakoCMS.Tests/ReadableContentTypesTests.cs` (`[Collection("Integration1")]`, `IntegrationTestFixture`, the `TenantProfileSeed` helpers `PagesBlockSchemaTests` uses)

**Interfaces:**
- Produces, in `Models.cs`:

```csharp
internal sealed record ReadableTypeSummary(string Name, string DisplayName);
internal sealed record ReadableTypeList(IReadOnlyList<ReadableTypeSummary> Items);
internal sealed record ReadableField(
    string Name, string Type, bool IsRequired,
    /* ValidationRules, Options, Multiple, ItemType, ReferenceType: same types as on FieldDefinition */
    string? BlockSchema); // "/api/blocks/schema" when Type is "blocks", else null
internal sealed record ReadableTypeResponse(
    string Name, string DisplayName, string Description, bool IsSingleton,
    IReadOnlyList<ReadableField> Fields, LifecycleDefinition? Lifecycle, List<UniquenessRule>? Uniqueness);
```

- JSON (camelCase) is the contract the client's Task 5 reads.

- [ ] **Step 1: Write the failing tests:**
  - `A_reader_gets_the_shape_of_a_type_it_may_read`: seed the blog blueprint, a role with read on `post`, a user in it; GET `/api/contents/types/post` is 200 with `name == "post"`, a `Title` field with `isRequired` true, and `fields` does not contain a property named `roles` or `sensitivity`.
  - `A_type_the_caller_may_not_read_is_404`: same user, GET `/api/contents/types/page` is 404.
  - `An_unknown_type_is_404`.
  - `A_conditional_read_rule_still_gets_the_shape`: a role whose read on `post` is limited to own entries gets 200.
  - `The_list_names_only_readable_types`: GET `/api/contents/types` lists `post` and not `page`.
  - `A_content_read_key_needs_no_admin`: a key with only `content:read`, acting as that user, gets 200 on both routes. Use the `StoreKeyAsync` pattern from `ApiKeyIntegrationTests`.
  - `Types_named_erase_and_share_links_are_ordinary_reads`: create types `erase` and `share-links`, give the role read on them, a `content:read` key gets 200 for each.
  - `A_lifecycle_type_lists_its_transitions`: a type with a lifecycle returns its states and transitions in `lifecycle`.
  - `A_blocks_field_points_at_the_block_schema`: a `blocks` field has `blockSchema == "/api/blocks/schema"`.
  - `An_account_shared_type_is_found`: a type the tenant gets from its account (`AccountContentTypes.ForTenantAsync`) resolves like an own type.

- [ ] **Step 2: Queue `ReadableContentTypesTests` and confirm they fail** (404 on every route).

- [ ] **Step 3: Implement both endpoints.** `Get("/api/contents/types")` and `Get("/api/contents/types/{name}")`, signed-in callers only, no capability. Resolve types the way `ContentType/Get/Endpoint.cs` does (own plus account-shared). The per-type check is `IPermissionResolver.CanPerformActionAsync(user, name, "read", null, ct)`; a false answer is 404, as is an unknown name. Map `FieldDefinition` to `ReadableField` without role lists or sensitivity. Confirm in a test run that `/api/contents/{id}` does not swallow `types` (it binds a Guid, as `counts` relies on).

- [ ] **Step 4: Queue the class again.** Expected: all pass.

- [ ] **Step 5: Commit** `add read-only content type shape routes for content readers`

### Task 3: keys reading the tree and block schema, and idempotent creates by key

**Files:**
- Test: `BarakoCMS.Tests/ApiKeyAuthoringReadsTests.cs` (`Integration1`)
- Modify: `BarakoCMS.Pages/Features/Tree/Endpoint.cs` only if the tree test fails

**Interfaces:** none new.

- [ ] **Step 1: Write the tests:**
  - `A_content_read_key_reads_only_the_pages_its_role_may_read`: two pages, the role may read one (by condition); the key's tree lists exactly that one.
  - `A_content_read_key_reads_the_block_schema`: 200 after an admin PUTs a schema.
  - `A_content_write_key_cannot_put_the_block_schema`: PUT with a `content:write` key is 403.
  - `A_create_repeated_with_one_idempotency_key_makes_one_entry`: two POSTs to `/api/contents` with the same `Idempotency-Key` from one key; one entry exists and both responses carry the same id.
  - `A_dry_run_then_a_real_create_with_one_idempotency_key_makes_one_entry`: dry run then real create with the same header; one entry exists.

- [ ] **Step 2: Queue the class.** The tree, schema and write tests depend on Task 1 and should pass. If an idempotency test fails, stop and report the server behaviour to the coordinator: the CLI's `--idempotency-key` promise depends on it, and the fix belongs in `IdempotencyFilter` with its own failing test first.

- [ ] **Step 3: Commit** `cover key reads of the page tree and block schema, and idempotent creates`

### Task 4: core PR

- [ ] Open the PR from `core/801-cli-schema-routes` (body: what changed, the test classes, links #801 #892). Run the repo's adversarial review, fix, merge with `gh pr merge N`, confirm it merged. Release as a minor version through the repo's release flow; if that needs Arnel's hand, ask once.

---

## Part 2: client (`~/lean-wt/client-cli`, branch `feat/authoring-calls`)

### Task 5: transport headers, authoring calls, version 0.4.0

**Files:**
- Modify: `src/transport.ts`, `src/resources/contents.ts`, `src/resources/content-types.ts`, `src/types.ts`, `src/client.ts`, `src/index.ts`, `package.json` (version `0.4.0`), `README.md`
- Create: `src/resources/blocks.ts`, `src/resources/pages.ts`
- Test: `src/client.test.ts` (extend `mockFetch` to take `headers` per response)

**Interfaces:**
- Produces:

```ts
// transport.ts
requestWithHeaders<T>(opts: RequestOptions): Promise<{ body: T; headers: Headers }>; // request() calls it

// types.ts
interface FieldDiff { field: string; before?: unknown; after?: unknown }   // match the server's FieldDiff names
interface DryRunResult { dryRun: true; changes: FieldDiff[] }
interface ReadableField { name: string; type: string; isRequired: boolean; validationRules?: unknown;
  options?: unknown; multiple?: boolean; itemType?: string; referenceType?: string; blockSchema?: string | null }
interface ReadableContentType { name: string; displayName: string; description: string; isSingleton: boolean;
  fields: ReadableField[]; lifecycle?: unknown; uniqueness?: unknown }
ListContentQuery gains search?: string

// contents
get(id): Promise<Content & { etag?: string }>
bySlug(type: string, slug: string): Promise<Content & { etag?: string }>
create(input, opts?: { dryRun?: boolean; idempotencyKey?: string }): Promise<WriteResult | DryRunResult>
update(id, input & { version?: number }, opts?: { ifMatch?: string; dryRun?: boolean }): Promise<(WriteResult & { etag?: string }) | DryRunResult>
transition(id: string, name: string): Promise<{ message?: string }>
schedule(id: string, at: { publishAt?: string; unpublishAt?: string }): Promise<unknown>
rollback(id: string, versionId: string): Promise<WriteResult>
erase(id: string): Promise<void>

// contentTypes
readable(): Promise<string[]>                         // GET /api/contents/types, item names
get(name: string): Promise<ReadableContentType>       // GET /api/contents/types/{name}

// new resources on BarakoClient
blocks: { schema(): Promise<unknown> }                // GET /api/blocks/schema
pages: { tree(): Promise<unknown> }                   // GET /api/pages/tree
```

- [ ] **Step 1: Write the failing tests**, one `it` per call, each asserting method, URL, body and headers against `mockFetch`:
  - `get` returns `etag` from the `ETag` response header, and `undefined` when there is none.
  - `update` with `ifMatch` sends `If-Match`; with `dryRun` sends `dryRun: true` in the body and returns `{ dryRun: true, changes }`.
  - `create` with `idempotencyKey` sends `Idempotency-Key`; with `dryRun` sends `dryRun: true`.
  - `list({ search: "a b" })` sends `search=a%20b`.
  - `bySlug("post", "a/b")` calls `/api/contents/by-slug/post/a%2Fb`.
  - `transition` sends `{ id, transition: name }` to `/status`; `schedule` sends `{ id, scheduledPublishAt, scheduledUnpublishAt }` to `/schedule`.
  - `rollback` POSTs `/api/contents/{id}/rollback/{versionId}`; `erase` DELETEs `/api/contents/{id}/erase`.
  - `contentTypes.readable()` returns `["post"]` from `{ items: [{ name: "post", displayName: "Post" }] }`.
  - The existing tests still pass unchanged (no signature break).

- [ ] **Step 2: Queue `src/client.test.ts` and confirm the new tests fail.**

- [ ] **Step 3: Implement** in the files above. Path segments go through `pathSegment`.

- [ ] **Step 4: Queue again, plus `npm run typecheck` and `npm run build`.** Expected: all pass.

- [ ] **Step 5: Commit** `add the authoring calls the CLI needs`. PR, adversarial review, merge. Publishing 0.4.0 goes through the repo's publish workflow; if that needs Arnel's hand, ask once.

---

## Part 3: CLI (`~/lean-wt/client-cli`, branch `feat/barako-cli`)

### Task 6: workspace, package, connection, output and the command table

**Files:**
- Modify: root `package.json` (`"workspaces": ["cli"]`, root scripts run both packages), `.github/workflows/ci.yml` (typecheck, test and build `cli/` too)
- Create: `cli/package.json` (`name @baryodev/barako`, `version 0.1.0`, `bin { "barako": "./dist/main.js" }`, `engines { "node": ">=22.23.4" }`, dependency `@baryodev/barako-client: ^0.4.0`), `cli/tsconfig.json`, `cli/tsup.config.ts` (ESM only, shebang banner)
- Create: `cli/src/output.ts`, `cli/src/connect.ts`, `cli/src/registry.ts`, `cli/src/main.ts`
- Test: `cli/src/output.test.ts`, `cli/src/connect.test.ts`, `cli/src/main.test.ts`
- Modify: the spec's Connection section (drop the tenant line, say why)

**Interfaces:**
- Produces:

```ts
// output.ts
type ErrorCode = "usage" | "validation" | "rule" | "conflict" | "version_required"
  | "not_found" | "unauthorized" | "forbidden" | "server" | "network";
class CliError extends Error { constructor(code: ErrorCode, message: string, opts?: { status?: number; details?: unknown }) }
function codeForStatus(status: number): ErrorCode;
function exitFor(code: ErrorCode): number;                       // table in Global Constraints
function toCliError(err: unknown): CliError;                     // BarakoError, fetch TypeError, CliError
function render(outcome: { ok: true; result: unknown } | { ok: false; error: CliError },
  secret: string | undefined): { stream: "stdout" | "stderr"; text: string; exit: number };

// connect.ts
interface Env { BARAKO_URL?: string; BARAKO_API_KEY?: string }
function connect(env: Env, flags: { url?: string; keyFile?: string },
  readFile: (p: string) => string): { client: BarakoClient; secret: string };

// registry.ts
interface ArgSpec { name: string; kind: "positional" | "flag"; type: "string" | "boolean" | "number" | "string[]";
  required?: boolean; description: string }
interface RunContext { client: BarakoClient; readFile(path: string): string }
interface Command { name: string; summary: string; args: ArgSpec[];
  run(ctx: RunContext, args: Record<string, unknown>): Promise<unknown> }
const commands: Command[];  // later tasks append to this table

// main.ts
async function run(argv: string[], env: Env, io: { readFile; write(stream, text) }): Promise<number>;
```

- [ ] **Step 1: Write the failing tests:**
  - `codeForStatus`: 400 `validation`, 422 `rule`, 409 and 412 `conflict`, 428 `version_required`, 404 `not_found`, 401 `unauthorized`, 403 `forbidden`, 500 and 503 `server`.
  - `exitFor`: every code to its number from the table.
  - `render` of a failure whose message and details contain the secret prints `[redacted]` in their place, and the secret appears nowhere in `text`.
  - `toCliError(new TypeError("fetch failed"))` is `network`, exit 6.
  - `connect`: missing URL is `usage`; missing key is `usage`; `--key-file` wins over the env and trailing newlines are trimmed.
  - `run(["content", "list", "post", "--api-key", "x"])` exits 1 and the message tells the agent to use `BARAKO_API_KEY` or `--key-file`.
  - `run(["commands"])` prints `{ ok: true, result: [...] }` where each item has `name`, `summary`, `args`.
  - `run(["nope"])` exits 1 with code `usage`.
- [ ] **Step 2: Queue `cli/` tests and confirm they fail.**
- [ ] **Step 3: Implement.** `main.ts` matches the longest command name from argv (`content list` before `content`), parses the rest with `parseArgs` built from the command's `args`, connects, runs, renders. `-o text` is accepted; its format is the builder's call.
- [ ] **Step 4: Queue again, typecheck, build.** Expected: pass.
- [ ] **Step 5: Commit** `add the barako CLI package with its output contract`

### Task 7: `--set`, `--set-file`, `--file` and the merge

**Files:**
- Create: `cli/src/fields.ts`
- Test: `cli/src/fields.test.ts`

**Interfaces:**
- Produces:

```ts
function parseSets(sets: string[], setFiles: string[], readFile: (p: string) => string): Record<string, unknown>;
function readDataFile(path: string, readFile: (p: string) => string): Record<string, unknown>;
function mergeData(current: Record<string, unknown>, patch: Record<string, unknown>): Record<string, unknown>; // top-level replace
```

- [ ] **Step 1: Write the failing tests:**
  - `Title=Hello` gives `{ Title: "Hello" }`; `Title=a=b` gives `"a=b"`; `Title=` gives `""`.
  - `Featured:=true` gives `true`; `Tags:=["a","b"]` gives the array; `Count:=bad` is `usage`.
  - `=x` (empty name) and `Title` (no `=`) are `usage`.
  - The same name twice across `--set` and `--set-file` is `usage`.
  - `--set-file Body=post.md` reads the file as a string.
  - `readDataFile` of `[1,2]` or `"x"` is `usage`; of `{ "a": 1 }` returns it.
  - `mergeData({ a: 1, b: { x: 1 } }, { b: { y: 2 } })` is `{ a: 1, b: { y: 2 } }` (top level only, by design).
- [ ] **Step 2: Queue and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Queue again.** Expected: pass.
- [ ] **Step 5: Commit** `parse field values for the barako CLI`

### Task 8: read commands

**Files:**
- Create: `cli/src/commands/read.ts` (appends to `commands`)
- Test: `cli/src/commands/read.test.ts` (fake `BarakoClient` object, no network)

**Interfaces:**
- Consumes: Task 5 client calls, Task 6 `Command`.
- Produces commands `types`, `schema <type>`, `content list <type> [--status] [--search] [--limit] [--all]`, `content get <id> | --slug <type>/<slug>`, `content history <id>`, `pages tree`.

- [ ] **Step 1: Write the failing tests:**
  - `content list post --status published` calls `list({ contentType: "post", status: 1 })`; `--status draft|published|archived|scheduled` map to 0, 1, 2, 3; any other value is `usage`.
  - `--limit` sets `pageSize`, default 20. `--all` follows pages and stops at 5000 entries with `truncated: true` in the result.
  - `content get --slug post/hello` calls `bySlug("post", "hello")`; `--slug post` (no `/`) is `usage`; both an id and `--slug` is `usage`.
  - `content get` result includes `version` and leaves out `etag`.
  - `schema post` returns `contentTypes.get("post")` as is.
- [ ] **Step 2: Queue and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Queue again.** Expected: pass.
- [ ] **Step 5: Commit** `add the barako read commands`

### Task 9: write commands

**Files:**
- Create: `cli/src/commands/write.ts`, `cli/src/commands/status.ts`
- Test: `cli/src/commands/write.test.ts`, `cli/src/commands/status.test.ts`

**Interfaces:**
- Consumes: Tasks 5, 6, 7.
- Produces commands `content create`, `content update`, `content publish|unpublish|archive|restore`, `content transition <id> <name>`, `content schedule <id> --publish-at <iso> [--unpublish-at <iso>]`.

- [ ] **Step 1: Write the failing tests:**
  - `create post --set Title=x` sends `status` 0 (draft); with `--publish` sends 1; `--idempotency-key k` passes `idempotencyKey: "k"`; `--dry-run` passes `dryRun` and the result has `dryRun: true` and `changes`.
  - `update id --set Title=y`: one `get`, then `update` with the merged `data`, `version` from the get, and `ifMatch` equal to the get's `etag`.
  - `update` never sends `version: 0`; a get answering `version: 0` is `server` error, no write.
  - `update --expect-version 4` when the get says 5: exit 3 `conflict`, `details.currentVersion == 5`, no `update` call.
  - Event-sourced: a get with no `etag` sends no `ifMatch`; a 409 from `update` triggers one more `get` and exits 3 with `details.currentVersion`.
  - A 412 from `update` behaves the same; a 428 is `version_required`, exit 3.
  - `update --replace --file e.json` sends the file as `data` with no merge.
  - `publish`, `unpublish`, `archive` call `setStatus` with 1, 0, 2.
  - `restore` on an archived entry calls `setStatus(id, 0)`; on a published entry exits 1 `usage` and says to use `unpublish`, with no write.
  - `publish --dry-run` makes no write and returns `{ dryRun: true, checkedBy: "client", from: "draft", to: "published" }`.
  - `publish` on a lifecycle type: the server's 400 becomes exit 2 and the message names `barako content transition`.
  - `schedule` with a non-ISO `--publish-at` is `usage`.
- [ ] **Step 2: Queue and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Queue again.** Expected: pass.
- [ ] **Step 5: Commit** `add the barako write commands with version-checked updates`

### Task 10: rollback and erase

**Files:**
- Create: `cli/src/commands/destructive.ts`
- Test: `cli/src/commands/destructive.test.ts`

**Interfaces:**
- Produces commands `content rollback <id> <versionId> --confirm <id>`, `content erase <id> --confirm <id>`.

- [ ] **Step 1: Write the failing tests:** missing `--confirm` is `usage` with no call; `--confirm` with a different id is `usage` with no call; a matching `--confirm` calls `rollback` or `erase`; a 403 from the server (key without `content:destructive`) is exit 5 and the message names the scope.
- [ ] **Step 2: Queue and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Queue again.** Expected: pass.
- [ ] **Step 5: Commit** `add barako rollback and erase behind --confirm`

### Task 11: integration suite, README and agent guide

**Files:**
- Create: `cli/test/compose.yml` (the public `ghcr.io/baryodev/barako-cms` image at the core release from Task 4, plus Postgres), `cli/vitest.int.config.ts`, `cli/test/content.int.test.ts`, `cli/README.md`, `cli/AGENTS.md` (the agent guide)
- Modify: root `README.md` (one line pointing at the CLI)

**Interfaces:**
- Consumes: the built `cli/dist/main.js`, run as a child process with `BARAKO_URL` and `BARAKO_API_KEY` set.

- [ ] **Step 1: Write the suite.** Setup through the public API only: sign in as the first-run admin the compose file seeds, apply the blog blueprint, create a role with read, create and update on `post` and `page`, a user in it, and a key with `content:read` and `content:write` for that user. Cases, in order:
  - `schema post` exits 0 and lists `Title`.
  - `content create post --set Title=It --set Slug=it-<random> --set-file Body=<tmp md>` exits 0, entry is a draft.
  - `content update <id> --set Title= --dry-run` (empty required title) exits 2, and a following `get` shows the title unchanged.
  - `content update <id> --set Title=New --expect-version <old>` after another update exits 3.
  - `publish`, `archive`, `restore` exit 0 and `get` shows 1, 2, 0.
  - `content history <id>` lists the versions.
  - `content erase <id> --confirm <id>` exits 5 (the key has no `content:destructive`).
  - stdout and stderr of every run never contain the key.
- [ ] **Step 2: Queue the integration config** (`brain queue add ~/lean-wt/client-cli package.json --filter cli/test/content.int.test.ts`; Docker needed, so not on the remote lane). Expected: pass.
- [ ] **Step 3: Write the README** (install, env, every command with one example, output and exit codes) **and `AGENTS.md`** (the order for "write a post": `schema`, `create --dry-run`, `create`, `get`, `update --expect-version`, `publish`; archive to take down, never erase).
- [ ] **Step 4: Commit** `add the barako integration suite and agent guide`. PR, adversarial review, merge. Publishing `@baryodev/barako` 0.1.0 goes through the publish workflow; ask Arnel once if it needs him.

### Task 12: hand-off for barakocms.com

- [ ] Write the steps Arnel runs (not done by an agent): create a role allowed to read, create and update `post` and `page`, a user in it, and a key with `content:read` and `content:write`; store it where the agent reads it. Post them in the CLI PR.
- [ ] The same hand-off names the barakocms.com upgrade that has to come first: rebuild the private CMS image at the core release from Task 4 (`scripts/build-cms-image.sh --core-ref vX.Y.Z` in barakocms-pro, arm64), get it onto the host (copy over ssh, or the read-only GHCR login in brain #11389), and set the new `BARAKO_TAG`. Until then `schema`, `types` and `pages tree` fail there; create, update and publish still work.
