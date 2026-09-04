# Reporting which modules an instance runs

`GET /api/modules` answers one question: which modules did this instance see at startup, and which
of them run.

Nothing else reported it. A deployment is core plus whatever modules the host added or discovery
found, filtered by `BarakoCMS:Modules:Enabled`, and an operator, an agent or a client library all
needed the same answer for the same reason: a call into a module the instance does not run should
fail with a sentence rather than a bare 404, and "installed but off" is a different sentence from
"not installed".

## The response

```
GET /api/modules
Authorization: Bearer <token>
```

```json
{
  "items": [
    { "name": "Accounting", "contractVersion": 0, "enabled": true, "schemaState": "ready", "schemaChanges": [] },
    { "name": "Files", "contractVersion": 0, "enabled": false, "schemaState": "unknown", "schemaChanges": [] }
  ],
  "page": 1,
  "pageSize": 100,
  "totalItems": 2,
  "totalPages": 1,
  "hasNextPage": false,
  "hasPreviousPage": false
}
```

The usual collection envelope, not a bare array. Every collection endpoint in 4.x returns this shape
and the root type is frozen, so a bare array is not available even for a list this short.
`ListEnvelopeTests` holds that.

`name` is `IBarakoModule.Name`, verbatim, and it is the name the host registered under.

`contractVersion` is `IBarakoModule.ContractVersion`. Zero means the module stated no version, which
the contract accepts, so zero is an answer rather than a missing value. See `ModuleContract` for what
the number covers.

`enabled` says whether the module runs in this process. False means the host added it or discovery
found it and `BarakoCMS:Modules:Enabled` left it off; its endpoints answer 404 and its data is still
in the database. A module that is not installed at all is not listed. See
[MODULES.md](../MODULES.md#choosing-which-modules-run).

`schemaState` is what the schema preflight found for the module at boot: `ready` when nothing it
registered would be refused by the store's `AutoCreate` policy, `needs-migration` when it wanted a
change to an existing database object, and `unknown` when the preflight did not run for it, either
because `BarakoCMS:Modules:SchemaPreflight` is off or because the module is not enabled and so
registered no schema. `schemaChanges` lists the existing objects it wanted to change by qualified
name, and is empty in every other state. A `CreateOnly` store refuses to boot on `needs-migration`,
so that value is only ever seen on a store that applied the change, which is the point: run the
check in development and read off what production would refuse. See
[MODULES.md](../MODULES.md#schema-preflight).

Today every first-party module but Email.Smtp answers zero for `contractVersion`: the others do not
override the property. So this endpoint confirms that a deployment picked a module up, and mostly
does not yet tell you which contract version it thinks it is talking to. That becomes useful as
modules start declaring one.

Items are ordered by name, ascending, always. `sortOrder` is inherited from the shared list request
and is not read here, so a client that sends `desc` still gets ascending. Registration order decides
which module configures services first, which is meaningful to the host and meaningless to a caller,
so two calls and two deployments of the same set agree on the order.

A host running no modules gets `items: []` and a 200. "None" is an answer, and a 404 would be
indistinguishable from a route that never shipped, which is exactly what a client library asking this
question is trying to tell apart.

## What it does not report

Name, contract version, enabled, schema state and the objects behind it, and nothing else. A module
knows its configuration section, its assemblies and therefore its file paths on the host, and none
of that is a fact about the module: it is a description of the deployment. `schemaChanges` names
database objects, which is the one deployment fact here, and it is there because the operator who
reads it is the one who has to apply the migration. `ModulesEndpointTests` asserts the property
names on each item are exactly `name`, `contractVersion`, `enabled`, `schemaState` and
`schemaChanges`, so a field added later has to be added there too, in a line somebody reviews.

## Authorisation

`Roles("SuperAdmin", "Admin")`. This is one of the two core routes still gated on a role name rather
than a capability, and it is pinned as such by
`RoleGateTests.The_core_routes_still_on_a_role_name_are_the_two_that_are_meant_to_be`, so it cannot
be forgotten and no third one can join it quietly.

Monitoring used to be the argument for leaving it: the nearest neighbour by purpose, gating the same
way. That stopped being true when monitoring moved to `view_monitoring`, so the reason recorded here
is now the pinned list rather than a neighbour.

Not anonymous, and not reachable with an API key. A module list tells a caller which surfaces an
instance exposes, which is reconnaissance. `ApiKeyScopeProcessor` confines API keys to the content
surface and denies everything else, so this needs a human JWT: a CLI that wants it has to log in
rather than present a key.

It is deliberately not gated on a `SystemCapabilities` name. Every capability in that vocabulary
covers a management surface this endpoint neither reads nor writes, and inventing one now would have
to be added to `SystemCapabilities.DefaultsFor("Admin")` to reach an Admin, where
`DataSeeder.ApplyCapabilityDefaults` leaves an already-backfilled role alone. An existing Admin would
never receive it, and with `Auth:LegacyRoleFallback=false` that Admin is locked out of the endpoint
with nothing in the diff that looks like a lockout. The instance-inspection surface gets a capability
when it is migrated as a group and one name can be chosen for the whole of it.
