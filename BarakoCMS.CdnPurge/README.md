# BarakoCMS.CdnPurge

Purges Cloudflare by cache tag when content publishes. Adds a `CdnPurge` workflow action.

Set `Modules:CdnPurge:ZoneId` and `Modules:CdnPurge:ApiToken`, then add `{ "Type": "CdnPurge", "Parameters": {} }`
to a workflow that triggers on publish. See `docs/delivery-api.md`, "Purging on publish".
