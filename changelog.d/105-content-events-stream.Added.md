- **A server-sent event stream of content changes.** `GET /api/public/events` streams
  `content.published`, `content.updated` and `content.unpublished` for the tenant, filterable with
  `?type=`. Every payload is produced by the same projection the REST reads use, so a Sensitive
  field is masked in the stream for the same reason it is masked on `GET /api/public/{type}/{slug}`,
  and a subscriber on one tenant never receives another tenant's change. Off by default:
  `Delivery:Events:Enabled` turns it on, `Delivery:Events:MaxConnections` (100) caps open streams
  per instance, and a keepalive goes out every 15 seconds. Fan-out is in process, so with several
  API instances each streams only the writes it handled; `docs/delivery-api.md` says so. Closes #105.
