# Content access control: CRUD RBAC + field/document sensitivity

> The admin UI this note plans for is barakoBrew, now in its own repository
> ([BaryoDev/barakoBrew](https://github.com/BaryoDev/barakoBrew)). The API side is unchanged.

> What exists today vs what needs building, for per-role CRUD, row-level scope,
> and granular field sensitivity on content types. All of this is core (content
> types + RBAC already live in core), not a module.

There are three layers. Two already work. One (sensitivity) is a hardcoded POC
that needs generalizing.

All three are enforced in C#, and that is a decision rather than an accident: `DECISIONS.md` D11.
Layers 1 and 2 are `IPermissionResolver`; layer 3 is `SensitivityService`, which is the reason for
the decision. Row-level security filters rows and has nothing to say about which fields inside one a
caller may see, so moving the boundary into Postgres would leave the most sensitive layer behind.

The layer 2 conditions compile to a SQL predicate where they can (#445), so `GET /api/contents` for
a named content type pages and counts in the database instead of loading the collection. That
changed where the rules are *evaluated*, not where they are enforced: the predicate is built from
the same rules `IPermissionResolver` reads, and the per-item check still runs over the page it
returns.

Where a rule cannot be compiled faithfully the compiler declines and the endpoint loads everything
and checks per item, exactly as it did before. `PermissionPredicateCompiler` lists what it refuses
and why. Declining costs a slow query; guessing would cost somebody a row they may not read, so the
refusals are deliberate and the list is meant to grow slowly.

`PermissionPredicateAgreementTests` runs generated rules and generated content through both
evaluators and asserts they select the same rows. Two evaluators for one condition language drift,
and the drift appears as rows silently appearing or vanishing rather than as an error, so that test
is the only thing that can tell you it has happened. Deleting it turns the compiler into a
liability.

## Layer 1: CRUD per content type per role (already works)

This is exactly the treasurer/secretary/admin ask, and it is built:

- `Role.Permissions` is a `List<ContentTypePermission>`.
- `ContentTypePermission` = `ContentTypeSlug` + `Create` / `Read` / `Update` /
  `Delete`, each a `PermissionRule { Enabled, Conditions }`.
- `PermissionResolver` enforces it: **additive union** across a user's roles
  (granted if ANY role allows), **SuperAdmin bypasses**, conditions evaluated
  per row.
- Content endpoints (Create/List/Get/Update/Delete/ChangeStatus) call
  `CanPerformActionAsync(user, contentTypeSlug, action, content)`.

So your example is pure configuration, no code:

| Role      | payment (content type)                          |
| --------- | ----------------------------------------------- |
| Treasurer | Read ✔, Create ✘, Update ✘, Delete ✘            |
| Secretary | Read ✔, Create ✔, Update ✔, Delete ✘            |
| Admin     | Read ✔, Create ✔, Update ✔, Delete ✔            |
| Member    | Read ✔ (own only, see Layer 2), rest ✘          |

Set these via the Roles endpoints / admin UI. Because logic is additive, a user
with both Treasurer and Secretary gets the union.

## Layer 2: Row-level scope (already works)

`PermissionRule.Conditions` uses Directus/Strapi-style predicates evaluated by
`IConditionEvaluator`, e.g. a Member reads only their own rows:

```json
{ "Read": { "Enabled": true, "Conditions": { "memberId": { "_eq": "$CURRENT_USER" } } } }
```

## Layer 3: Field + document sensitivity

This is the "Employee has SIN + birthday sensitive, rest viewable" ask, plus the
Off / SensitiveOnly / All mode. It is built. `SensitivityService` reads each
field's `Sensitivity` off the content type's own schema, masks on read and
reverts on write, and `Sensitivity:Mode` selects the mode. See "What it does now"
below for the shipped behaviour.

### What it started as, kept for the history (a hardcoded proof of concept)

The section below describes the state this replaced. `SensitivityFilter` no
longer exists and nothing names `AttendanceRecord` in the filtering path.

- **Document-level** works: `Content.Sensitivity` enum (`Public` / `Sensitive` /
  `Hidden`). But the role mapping is **hardcoded in the filter**: `Hidden` =>
  only SuperAdmin, `Sensitive` => only SuperAdmin or HR.
- **Field-level is hardcoded** in `SensitivityFilter` for exactly one content
  type: `AttendanceRecord`, where `SSN` is removed unless SuperAdmin and
  `BirthDay` is masked to `***` unless HR. There is `Console.WriteLine` debug
  spam and a duplicate, unused `SensitivityService`.
- Enforced as an `IGlobalPostProcessor` that scrubs the response `Data` after
  the endpoint runs.

It demonstrates the idea but is not data-driven, not configurable, and roles are
baked in.

### Target design (make it generic + granular + role-based)

**1. Put sensitivity on the field schema.** Extend `FieldDefinition`:

```csharp
public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Public;
public List<string> VisibleToRoles { get; set; } = new();   // who may see it when not Public
public FieldMask Mask { get; set; } = FieldMask.Remove;      // Remove | Redact("***") | Last4
```

So the `Employee` content type declares, per field:

| Field    | Sensitivity | VisibleToRoles   | Mask   |
| -------- | ----------- | ---------------- | ------ |
| SIN      | Hidden      | [Admin]          | Last4  |
| Birthday | Sensitive   | [Admin, HR]      | Redact |
| Name     | Public      | n/a              | n/a    |
| Email    | Public      | n/a              | n/a    |

Result: a Treasurer who can Read `Employee` sees Name/Email but SIN as `***-6789`
and Birthday as `***`; HR also sees Birthday; Admin sees everything.

**2. Rewrite the filter to be data-driven.** Load the `ContentTypeDefinition`
for `content.ContentType` (cached), apply document-level (using a configurable
level->roles policy, not hardcoded SuperAdmin/HR), then per-field masking from
the schema. Delete the AttendanceRecord hardcode, the debug lines, and the
duplicate service.

**3. Mode switch** `Sensitivity:Mode`:

- **Off**: no scrubbing (dev / fully trusted internal tools).
- **SensitiveOnly**: scrub only fields/docs marked Sensitive or Hidden; Public
  flows through. This is the normal production mode.
- **All**: strict, any field not explicitly visible to the caller's role is
  withheld (lockdown / high-security tenants).

**4. Role mapping is data-driven.** A field's `VisibleToRoles` gives granular
control; when empty, fall back to a configurable default policy
(`Sensitive -> [HR, Admin]`, `Hidden -> [Admin]`). SuperAdmin always sees all.

**5. Protect the write path too.** Sensitivity today only scrubs reads. A
non-privileged role that can Update the content could still overwrite SIN. The
create/update validators must drop or reject sensitive fields the caller is not
allowed to see, so masking cannot be bypassed by writing.

**6. Admin UI.** The content-type editor gets per-field sensitivity controls
(level, roles, mask); the role editor already has the CRUD matrix.

### Where it lives

Core. It extends the existing content schema (`FieldDefinition`), the existing
RBAC (`Role` / `PermissionResolver`), and the existing `SensitivityFilter`. Not
a module.

## Update (2026-07-16): field-level sensitivity FIXED

The bug below is fixed. Sensitivity is now one schema-driven service
(`SensitivityService`) that Get, List, and History all call explicitly; the
broken global post-processor and the duplicate document-only service are gone.
Field masking is declared on the schema (`FieldDefinition.Sensitivity` +
`VisibleToRoles` + `Mask`) and driven by a `Sensitivity:Mode` config
(Off/SensitiveOnly). `List` no longer leaks (it scrubbed nothing before).

Proven by `SensitivityIntegrationTests` (real HTTP, 7/7): SuperAdmin sees all;
HR sees BirthDay but not SSN; a plain reader gets SSN removed and BirthDay
redacted on a Public doc, and nothing on a Sensitive/Hidden doc; List masks too.
Full suite: 189 passed, 0 failed. The fake `SensitivityTests.cs` (which
re-implemented the logic inline) was deleted.

## Update (2026-08-30): the write path is protected

`Features/Content/Update/Endpoint.cs` calls `ISensitivityService.ApplyWriteAsync`
before validation, reverting any field the caller may not see back to its stored
value. A role that can Update a record can no longer overwrite a field masked
out of its own reads (#371).

**Still open:** the admin UI has no per-field sensitivity controls, so a
content type's field sensitivity is set through the API.

## Update (2026-09-01): changing a field's sensitivity after the fact

`PUT /api/content-types/{name}/fields/{field}/sensitivity`, admin only. Until
this there was no update path on a content type at all, so the level a field was
created with was the level it kept.

It is deliberately not a general content-type update. Field names, field types
and a reference target are load-bearing once entries exist, and changing those
is a separate decision (#163).

The two directions are not the same operation.

**Raising** (Public towards Hidden) stops the value being served, and that is
all it does. The value is still in the entry's JSONB data, in every backup and
in the event stream. Nothing here erases it, and a document saying otherwise
would be wrong. Erasure is `DELETE /api/contents/{id}/erase`.

Raising is not finished when the definition changes, though. Anonymous search
matches against `Content.SearchText`, a column derived from whichever fields
were Public the last time each entry was written, so updating the definition and
stopping there would change what is *returned* and not what is *matched*: a
caller could still search for a value they may no longer read and learn which
entries contain it, one guess at a time. The endpoint rebuilds the search text
for every existing entry of the type, before the definition changes, and it does
so through an event (`ContentFieldSensitivityChanged`) so a later projection
rebuild cannot replay the old text back over it.

**Lowering** is a disclosure. Every value written while the field was masked
becomes readable to everyone who can read the type, retroactively, and to
anonymous callers as well when the type is publicly deliverable. It is allowed,
because refusing it would make raising a one-way door and a field marked
Sensitive by mistake would need direct database access to recover. It is allowed
only when the request sets `acknowledgeDisclosure`; the refusal names how many
existing entries the decision covers. It is recorded as
`contenttype.field.sensitivity.lowered` in the audit log, an action of its own so
it can be alerted on without reading the metadata of every change. Everything
else is `contenttype.field.sensitivity.changed`.

`VisibleToRoles` and `Mask` are replaced with the level rather than carried over
from the level being left behind, and a Public target clears both. A Public field
listing the roles that may see it reads as a restriction that is not there, and
leaving the list in place would silently reinstate a stale allowlist the next
time somebody raised the level.

The rebuild is synchronous and proportional to the number of entries of the
type. A background rebuild would answer 200 while the value was still matchable
anonymously, with nothing to tell the caller when it stopped being.

Still no admin UI for it: the endpoint is called directly.

## Tested (2026-07-16): the bug this replaced

Ran as real HTTP integration tests (Testcontainers Postgres, role tokens,
`GET /api/contents/{id}`). See `BarakoCMS.Tests/SensitivityIntegrationTests.cs`
and `ContentPermissionTests.cs`.

- **CRUD RBAC: PASS (5/5).** Create/read with and without permission return
  200/403 correctly. Per-content-type, per-role, additive. Solid.
- **Document-level sensitivity: PASS.** `Hidden` blanks data for non-SuperAdmin
  (`contentType` becomes `HIDDEN`); `Sensitive` clears data for non-HR/
  SuperAdmin; SuperAdmin sees everything.
- **Field-level sensitivity: FAIL (real bug).** HR and a plain reader both still
  receive `SSN` over the wire, deterministically, across two runs, even though
  the filter's own debug log prints "Removing SSN". The existing
  `SensitivityTests.cs` hid this by re-implementing the masking inline instead of
  calling the real code.

### Root cause

`Content/Get/Endpoint` builds the response and then calls
**`ISensitivityService.Apply(...)`**, which only does **document-level**
scrubbing. The **field-level** logic lives in a *different* class, the
`SensitivityFilter` global post-processor, whose edits do **not** reach the
serialized body for this endpoint. Two competing implementations; only the
document-level one is wired into the response. So field masking is dead code in
practice.

(One test, SuperAdmin reading an `AttendanceRecord`, fails with a Postgres error
rather than an assertion, a separate issue to dig into, not the masking bug.)

### Fix direction (folds into the design below)

Collapse to one path: apply sensitivity (document + field) in one service the
endpoints call before sending, driven by the field schema. Delete the
post-processor and the duplicate service. Then the granular design below is both
real and testable, and these red tests go green.

## How to verify what works today

The seeder creates an `AttendanceRecord` content type (fields incl. `SSN`,
`BirthDay`), three records marked `Sensitivity = Sensitive`, and roles
`SuperAdmin` + `HR`. Sign in as each and `GET /api/contents/{id}`:

- **SuperAdmin** sees `SSN` and `BirthDay`.
- **HR** sees `BirthDay`, not `SSN`.
- **Plain user** sees neither (`SSN` removed, `BirthDay` = `***`).

That proves the mechanism, and the work below made it configurable per field and
per type instead of hardcoded.

## Phasing, and where it got to

1. **Done.** Field sensitivity lives on `FieldDefinition` and the filter is data
   driven. The AttendanceRecord hardcode and the duplicate service are gone.
2. **Done.** `Sensitivity:Mode` selects `Off`, `SensitiveOnly` or `All`.
3. **Write-path protection done**, in `ISensitivityService.ApplyWriteAsync`: a
   caller who cannot see a field cannot set it, and omitting it is not a way to
   delete it. Admin UI per-field toggles are still outstanding.

## Administrative endpoints: system capabilities

Everything above is about content. Administrative endpoints (roles, tenants, users,
settings) are a separate surface, and they used to gate on hardcoded role names:

```csharp
Roles("SuperAdmin", "Admin");
```

Roles are runtime data, so that could not work in either direction. A role created
through `POST /api/roles` could never be granted anything without a code change and a
release, and a role someone named `Editor` silently picked up whatever `Editor` was
written into. See issue #272.

`Role.SystemCapabilities` is what those endpoints ask for now.

### Declaring a gate

In `Configure()`, instead of `Roles(...)`:

```csharp
Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
```

The first argument is the capability a caller needs. The rest are the role names the
endpoint gated on before, kept as a fallback so an existing deployment keeps working
across the upgrade (see below).

This replaces the role gate rather than adding to it. FastEndpoints combines role gates
with AND, so keeping `Roles("SuperAdmin")` alongside a capability would mean a caller
needed both, and a role created at runtime still could not reach anything.
Authentication is unchanged: an anonymous caller is refused 401 before any of this runs.

### Granting a capability

A capability is a string on the role, so this is configuration:

```json
PUT /api/roles/{id}
{ "name": "Auditor", "systemCapabilities": ["manage_roles"] }
```

`*` satisfies everything, including capabilities added later. SuperAdmin bypasses, the
same way it does for content permissions.

`SystemCapabilities.Known` is the vocabulary. It is deliberately short and grows one
area at a time, because the role gates it replaces are not uniform: `GET /api/settings`
is `Roles("SuperAdmin", "Admin")` while `PUT /api/settings/email` is `Roles("SuperAdmin")`,
so a single `manage_settings` invented ahead of the migration would have to pick one of
those and would hand out access it was meant to preserve. Users split the same way, which
is why that area has three names and not one (see below).

### Discovering the vocabulary

`GET /api/capabilities` lists every name this instance understands. It is gated on `manage_roles`,
the same as reading roles, and it answers with the usual list envelope:

```json
{ "items": [
  { "name": "*", "source": "core", "note": "Satisfies every capability, including ones added after the role was written. A role holding it reaches every gated endpoint on this instance." },
  { "name": "manage_roles", "source": "core", "note": null },
  { "name": "view_ledger", "source": "Accounting", "note": null }
], "page": 1, "pageSize": 100, "totalItems": 38 }
```

The list is `SystemCapabilities.Known` plus every name a served endpoint declares through
`Definition.RequireCapability(...)`, read off the routing table at first use. That is the same
metadata `CapabilityGateProcessor` enforces, so a name is listed exactly when some endpoint asks for
it. A module you have not installed contributes nothing, and a module needs no new contract member to
be listed. `source` is `core` or the registered module's `Name`; a module whose endpoints are served
without the module itself being registered is named by its assembly instead.

### Unknown names on a role write

`POST /api/roles` and `PUT /api/roles/{id}` check `systemCapabilities` against that list. A name no
endpoint asks for grants nothing, since `Satisfies` is an exact match, so this is about telling the
operator rather than about access. `*` is always known.

`Roles:RefuseUnknownCapabilities` (env `Roles__RefuseUnknownCapabilities`) decides what happens to an
unknown name. Off, the default, is what always happened: the role saves. What is new is that the
unknown names are logged at warning and returned in the response as `unknownCapabilities`, so a
console can show them. Saving rather than refusing is deliberate: a module installed later that
declares the name starts working without the role being edited again.

On, the write is refused with a 400 whose entries name each unknown capability and point at
`GET /api/capabilities`. Nothing is saved.

### It is a lookup, not a claim

Capabilities are resolved per request from the caller's roles, not baked into the token.
A claim would be stale for as long as the token lives, so revoking a capability during
an incident would not take for up to 15 minutes and nothing would say so.
`CachedPermissionResolver` absorbs the cost, keyed per user and per tenant, and evicts on
the role and membership changes that can alter the answer.

### Upgrading

Two things keep an existing deployment working:

- The seeder backfills the capabilities its four system roles already had the access
  for, on the next start, so they are visible and editable rather than showing as roles
  with nothing. It adds whatever is missing from the default set rather than filling only
  an empty list, so a capability added to the vocabulary after your deployment upgraded
  still reaches your Admin.

  The cost of that, plainly: a default you have deliberately removed from a seeded system
  role comes back on the next restart, because nothing records that the removal was
  deliberate. If you need one gone for good, do not run the seeder. A role you created is
  untouched, since the defaults are keyed on the names the seeder creates.
- The gate can also honour the role names it replaced, which is what makes access survive
  on a host that never calls the seeder. From 4.0 that is off unless you ask for it.

`Auth:LegacyRoleFallback` (env `Auth__LegacyRoleFallback`) decides whether a role name
still opens the gate it used to. It was `true` through 3.x so an upgrade kept working
while roles had no capabilities yet. **From 4.0 it defaults to `false`**, because every
core and module endpoint now gates on a capability and the seeder gives a role the
capabilities it is missing rather than only filling an empty list. A seeded deployment
reaches everything it used to without the fallback.

Set it back to `true` if your roles are curated by hand, or while an upgrade is in
progress. The flag has not gone anywhere; what changed is which way it points when nobody
says.

### What is migrated so far

| Area | Capability | Routes | Seeded roles holding it |
| --- | --- | --- | --- |
| `Features/Roles/*` | `manage_roles` | `/api/roles` | SuperAdmin |
| `Features/Tenants/*` | `manage_tenants` | `/api/tenants` | SuperAdmin |
| `Features/Tenants/Members/*` | `manage_tenant_members` | `/api/tenants/members` | SuperAdmin, Admin |
| `Features/Users/*` | `manage_users` | `GET /api/users`, `POST /api/users/{id}/password` | SuperAdmin |
| `Features/Users/*` | `manage_user_membership` | `/api/users/{id}/roles`, `/api/users/{id}/groups` | SuperAdmin, Admin |
| `Features/UserGroups/*` | `manage_user_groups` | `/api/user-groups` and everything under it | SuperAdmin, Admin |
| `Features/ApiKeys/*` | `manage_api_keys` | `/api/api-keys` | SuperAdmin, Admin |
| `Features/Audit/*` | `view_audit_log` | `GET /api/audit` | SuperAdmin, Admin |
| `Features/Settings/*` | `manage_settings` | `/api/settings`, `GET /api/settings/email` | SuperAdmin, Admin |
| `Features/Settings/Email/*` | `manage_email_settings` | `PUT /api/settings/email`, `POST /api/settings/email/test` | SuperAdmin |
| `Features/ContentType/*` | `manage_content_types` | `/api/content-types` (and its `/api/schemas` alias), `POST /api/content-types/{name}/rebuild`, `POST /api/content-types/{name}/seo-fields` | SuperAdmin, Admin |
| `Features/Modules/*` | `view_modules` | `GET /api/modules` | SuperAdmin, Admin |
| `Features/ContentType/*` | `manage_public_delivery` | `PUT /api/content-types/{name}/public-delivery`, `PUT /api/content-types/{name}/fields/{field}/sensitivity` | SuperAdmin, Admin |
| `Features/Monitoring/*` | `view_monitoring` | `GET /api/monitoring/health`, `/k8s`, `/metrics` | SuperAdmin, Admin |
| `Features/Redirects/*` | `manage_redirects` | `/api/redirects`, `DELETE /api/redirects/{id}`, `POST /api/redirects/import` | SuperAdmin, Admin |
| `Features/Queries/*` | `manage_queries` | `/api/queries`, `/api/queries/{slug}`, `POST /api/queries/{slug}/preview` | SuperAdmin, Admin |
| `Features/Requests/*` | `manage_requests` | `/api/requests`, `/api/requests/{slug}`, `POST /api/requests/{slug}/dry-run/{contentId}` | SuperAdmin, Admin |
| `Features/Connectors/*` | `view_connectors` | `GET /api/connectors`, `GET /api/connectors/{slug}` | SuperAdmin, Admin |
| `Features/Connectors/*` | `manage_connectors` | `POST /api/connectors`, `PUT` and `DELETE /api/connectors/{slug}`, `POST /api/connectors/{slug}/test` | SuperAdmin, Admin |
| `Features/Workflows/*` | `manage_workflows` | `/api/workflows`, `/api/workflows/actions`, `/variables`, `/validate`, `/dry-run` | SuperAdmin, Admin |
| `Features/WorkflowRuns/*` | `view_workflow_runs` | `GET /api/workflow-runs`, `GET /api/workflow-runs/{id}`, `GET /api/workflows/{id}/debug` | SuperAdmin, Admin |
| `Features/WorkflowRuns/*` | `retry_workflow_actions` | `POST /api/workflow-runs/{id}/actions/{ordinal}/retry` | SuperAdmin, Admin |
| `Features/Content/History/*` | `rollback_content` | `POST /api/contents/{id}/rollback/{versionId}` | SuperAdmin, Admin |
| `Features/Content/Erase/*` | `erase_content` | `DELETE /api/contents/{id}/erase` | SuperAdmin |

Users is two capabilities because its old gates were two: listing accounts and resetting
someone's password were `Roles("SuperAdmin")`, while changing a user's roles and groups
was `Roles("SuperAdmin", "Admin")`. `manage_users` is the narrow one. Giving it to Admin
would have handed every Admin the user list, so Admin's defaults carry
`manage_user_membership` and `manage_user_groups` and not `manage_users`. See issue #443.

API keys and the audit log are two capabilities for the opposite reason: their old gates were
*identical*, so one name would have covered both and no seeded role would have noticed. They are
split because a role that reads the audit trail without being able to mint credentials is the
ordinary auditor case, and a single name makes it unexpressible. `view_audit_log` is named for
reading because the surface is one GET: entries are append-only and the chain is tamper-evident,
so there is nothing to manage.

Settings splits for the same reason Users does: reading settings and reading the email summary were
`Roles("SuperAdmin", "Admin")`, while changing the email settings and sending a test through them
were `Roles("SuperAdmin")`. Changing where the deployment's mail comes from redirects every password
reset and every verification token in it, so it is a takeover rather than an administrative tweak,
and it is exactly the change a compromised admin account makes. One `manage_settings` covering both
would have handed that to every Admin, so Admin's defaults carry `manage_settings` alone.

Connectors split read from write, and the argument is the surface's own. A connector is the only
document in core holding somebody else's credentials. The two GETs return the configuration and the
names of the secrets, never a value; the writes take secret values, and the probe spends them
against the configured base URL. Writing is credential handling twice over: it is where a token
enters the system, and where a base URL can be repointed, which redirects every request built on
that connector without touching a single request definition. The case against splitting is that
nothing about a connector is harmless, since the list alone says which third parties this deployment
talks to. That is why reading is gated too, at its own name. It is not a reason to make whoever
answers "where does the invoicing connector point" hold the grant that can repoint it.

Workflows split three ways. Authoring is one job: creating a workflow, reading the registered actions
and template variables, validating a definition and dry-running one. The dry run stays with authoring
because it executes nothing, and withholding the simulation from whoever wrote the workflow leaves
production as the only way to see what it does. Reading runs is a second job, and
`GET /api/workflows/{id}/debug` is in that half rather than with authoring, because what it returns
is the execution log of what already ran. Retrying is the third: the runner picks the attempt up and
the action happens for real, so "did the notification go out" needs the run list and must not carry
the ability to send it again.

Queries and requests are one capability each, including the preview and the dry run. The query
preview is the closer call, since it reads content rows and does not consult the per-role content
permissions. It stays with authoring because whoever can save a definition can point one at any
content type and attach it to a request, which sends the same rows to a third party; showing them to
the author is strictly less than that. What bounds the disclosure is `QueryRunner`, not the gate: a
query may only filter, sort and project fields whose sensitivity is `Public`, re-checked on every run
rather than only when the query was saved.

The two destructive content routes are two capabilities because their gates differed:
`POST /api/contents/{id}/rollback/{versionId}` was `Roles("SuperAdmin", "Admin")` and
`DELETE /api/contents/{id}/erase` was `Roles("SuperAdmin")`. One name would have to pick one of
those, and picking the wider one hands every Admin an irreversible delete. `erase_content` is the
only capability in the whole of #443 that Admin's defaults do not carry.

### Modules

Every first-party module is migrated too, and a module declares its own capability names rather than
core declaring them: core does not reference a module, and a third-party one is not in this
repository at all. A name a module declares is grantable the day the module ships: its endpoints put
it on the routing table, which is where `GET /api/capabilities` and the role write check read from.

| Module | Capability | Routes |
| --- | --- | --- |
| Accounting | `view_ledger` | `GET /api/accounting/accounts`, `/balances`, `/accounts/{code}/ledger` |
| Accounting | `post_journal_entries` | `POST /api/accounting/accounts`, `/journal-entries` |
| AI | `manage_search_index` | `POST /api/ai/index/{type}` |
| Analytics (Umami) | `view_analytics` | the five read endpoints under `/api/analytics` |
| Analytics (Umami) | `manage_analytics_websites` | `POST /api/analytics/websites` |
| Diagnostics | `manage_client_errors` | `GET /api/client-errors`, `POST /api/client-errors/{id}/resolve` |
| Email (Resend) | `view_email_events` | `GET /api/email-events` |
| Feature flags | `manage_feature_flags` | everything under `/api/feature-flags/admin` |
| Files | `upload_files` | `POST /api/files` |
| Portability | `export_content` | `GET /api/portability/export` |
| Portability | `import_content` | `POST /api/portability/import` |
| PWA | `view_pwa_installs` | `GET /api/pwa/installs` |

Three of those are splits of a gate that used to be one role list. Accounting splits reading the
books from writing to them, which is the separation every accounting system makes and which lets an
auditor read a ledger without being able to post to it. Analytics splits reading the numbers from
provisioning a website in somebody else's system using this deployment's credentials. Portability
splits export from import because the risks are opposite: export reads a whole tenant out in one
request, import writes a whole tenant in.

A module grants its own capabilities at seed time, to the roles its old `Roles(...)` gate listed,
using `ModuleCapabilities.GrantAsync`. Additive, idempotent, and it skips a role the host never
seeded rather than inventing one. SuperAdmin is not granted anything: it holds `*`, which satisfies a
capability from a module core has never heard of. A module you do not install grants nothing, because
its seeder never runs.
Content types split for the audit-log reason rather than the users reason: both gates were the same
role pair, so one name would have covered them and no seeded role would have noticed. They are split
because designing a schema and deciding what an anonymous caller can read are different jobs. Field
sensitivity is what decides whether a value is scrubbed on the way out, and public delivery decides
whether the route answers at all, so both are disclosure decisions rather than modelling ones. Admin
holds both by default, because Admin reached all five routes already and this narrows nothing.

### What is not migrated yet

None. Every core route that gates at all gates on a capability, and `RoleGateTests` pins that the set
still on a role name is empty, so a new endpoint reaching for `Roles(...)` fails the suite.

The last two moved in the same change. `GET /api/modules` asks for `view_modules`, named for reading
because it answers with two fields per module and manages nothing. `POST /api/content-types/{name}/seo-fields`
asks for `manage_content_types`, since adding fields to a content type is exactly what that capability
is. Admin holds both by default, matching what it reached before.

Third-party modules calling `Roles(...)` are unaffected and compile unchanged.
