# BarakoCMS.CdnPurge

Purges Cloudflare by cache tag when content publishes. Adds a `CdnPurge` workflow action.

## Install

```sh
dotnet add package BarakoCMS.CdnPurge
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
modules run (`BarakoCMS__Modules__Enabled=CdnPurge`). To name it by hand, put
`modules.Add(new BarakoCMS.CdnPurge.CdnPurgeModule())` in the `AddBarakoCMS` callback. See `MODULES.md`
in the repository.

Set `Modules:CdnPurge:ZoneId` and `Modules:CdnPurge:ApiToken`, then add `{ "Type": "CdnPurge", "Parameters": {} }`
to a workflow that triggers on publish. See `docs/delivery-api.md`, "Purging on publish".
