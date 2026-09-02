# Content access control: CRUD RBAC + field/document sensitivity

> What exists today vs what needs building, for per-role CRUD, row-level scope,
> and granular field sensitivity on content types. All of this is core (content
> types + RBAC already live in core), not a module.

There are three layers. Two already work. One (sensitivity) is a hardcoded POC
that needs generalizing.

All three are enforced in C#, and that is a decision rather than an accident: `DECISIONS.md` D11.
Layers 1 and 2 are `IPermissionResolver`; layer 3 is `SensitivityService`, which is the reason for
the decision. Row-level security filters rows and has nothing to say about which fields inside one a
caller may see, so moving the boundary into Postgres would leave the most sensitive layer behind.

Issue #445 will compile the layer 2 conditions to SQL predicates, so the list endpoint stops loading
a whole collection to return a page. It is not built. Today `GET /api/contents` narrows on content
type, status and search in the query and then loads every remaining row to check permission on each
one, so the cost still tracks the collection rather than the page. When #445 lands it will change
where the rules are *evaluated*, not where they are enforced.

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
