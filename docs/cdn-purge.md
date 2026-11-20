# CDN purge

Core purges a CDN by cache tag whenever a commit changes what public delivery serves, for a tenant
that turned purge on. Per-tenant settings say which CDN and with what credentials. A tenant that is
off sends nothing and costs nothing beyond one cached settings answer.

## Off by default

A tenant has no purge settings until an admin saves some, and `enabled` is false until they set it.
A tenant that is off, has no provider, or has no token gets no purger.

## What a tenant admin sets

`GET` and `PUT /api/tenant-settings/cdn-purge`, scoped to the request's tenant and gated on
`manage_settings`. Null leaves a field alone and an empty string clears it.

| Field | Meaning |
| --- | --- |
| `provider` | `none`, `cloudflare` or `fastly` |
| `enabled` | `true` needs a provider, a target id and a token, else 400 |
| `targetId` | The Cloudflare zone id or the Fastly service id. 1 to 64 letters and digits |
| `token` | The provider API token. At most 2048 characters, no line breaks or control characters |

The token is write only. It is encrypted with `ISecretProtector` before it is stored, and no
response has a field that could hold it. Reads say `tokenSet`. A tenant cannot set the provider's
host. The audit entry `settings.cdn-purge.tenant.changed` lists the field names that changed and
never a value. The token is visible ASCII only (no spaces, line breaks or other characters), up to
2048 long.

The default tenant has no row and gets a 400 on these routes. It reads host configuration:

```
Delivery:CdnPurge:Enabled      true to turn it on
Delivery:CdnPurge:Provider     cloudflare or fastly
Delivery:CdnPurge:TargetId     zone id or service id
Delivery:CdnPurge:ApiToken     the provider token
```

`Delivery:CdnPurge:BaseUrl:Cloudflare` and `:Fastly` override the provider's API root. They are host
configuration only, so a test can point the client at a stand-in. The stand-in host must also be in
`Webhooks:AllowedHosts`, because the guarded client blocks loopback and private addresses otherwise.

## The two providers

| Provider | Call | Auth | Body | Tags per call |
| --- | --- | --- | --- | --- |
| Cloudflare | `POST https://api.cloudflare.com/client/v4/zones/{zoneId}/purge_cache` | `Authorization: Bearer <token>` | `{"tags":[...]}` | 100 |
| Fastly | `POST https://api.fastly.com/service/{serviceId}/purge` | `Fastly-Key: <token>` | `{"surrogate_keys":[...]}` | 256 |

The limits are the ones in the vendors' documentation: the
[Cloudflare purge by tag page](https://developers.cloudflare.com/cache/how-to/purge-cache/purge-by-tags/)
and the [Fastly purging reference](https://www.fastly.com/documentation/reference/api/purging/).
A longer list goes out in several calls. The first call that fails stops the rest, and the calls before it were already sent. Purges are
idempotent, so the caller retries the whole list. The calls go through the guarded `ExternalApi`
HTTP client.

## Result

`PurgeTagsAsync` returns `Succeeded`, `Status`, `Error`, `Retryable` and `RetryAfter`. `Retryable` is
true for a network failure or timeout, 429 and 5xx, and false for 400, 401, 403, 404 and a blocked
address. A 429 carries the
provider's `Retry-After` when it sent one. The error text never contains the token, even when the
provider echoes it back. An empty tag list sends nothing and succeeds.

Tags come from `DeliveryTagSet.ForCommitAsync`, the function the response cache eviction uses too. It can fall back to the bare tenant tag, which purges the whole tenant, when it cannot tell what a commit touched. They are sent unchanged, so `:` and `%` in a
tag reach the provider as written.

## What triggers a purge

Every commit that changes delivered content, not only a publish: a create, an edit, a status change,
an archive, a batch or import, a scheduled publish, a workflow action, a collections push or sync, a
config apply, an erase, a history rollback, a sensitivity change, a type definition change and a
redirect change. The listener is on the document store, so it does not matter which writer made the
commit. It runs whether or not `Delivery:ResponseCache:Enabled` is on.

The tags are the same ones the response cache evicts, from one shared function: the entry, type and
sitemap tags of each changed entry, the type and sitemap tags of a changed definition, the redirects
tag for a redirect, and the whole tenant tag if the set cannot be worked out. Every tag carries the
tenant prefix, so a purge for one tenant never reaches another.

## How it is sent

The purge never runs inside the commit and a failed purge never fails the write.

1. Right after the commit, the listener writes one `PendingCdnPurge` row for the tenant with the
   tags. Inside a batch it waits for the batch commit and writes once. A rolled back batch writes
   nothing.
2. A background sender on each node takes rows older than the debounce window, merges the tags of a
   tenant, splits them to the provider's limit and calls the provider with a timeout. The same entry
   changed twice in the window goes out once. A commit during a flush goes in the next one.
3. On success the rows are deleted. On a failure that can be retried (network error, timeout, 429,
   5xx) the rows stay with a retry time that doubles from the base up to one hour. After the attempt
   cap the rows are dropped. A failure that cannot be retried (400, 401, 403, a blocked address)
   drops them at once.

Once the row is written it is durable, and a node that stops after that does not lose the purge. The tags
exist only in memory until the row is saved, so a stop between the content commit and the row write
loses that one purge, and the page then expires at its `max-age`. The same goes for a purge that is
dropped: the cached page expires at its `max-age`. Do not set the CDN TTL or
`max-age` longer than you can tolerate a stale page.

Two nodes do not send the same rows. A node claims rows with a versioned write, as build hooks do,
and a node that loses the write leaves the rows to the winner. A claim older than five minutes is
treated as abandoned. A stop in the middle of a provider call lets the claim go, so the rows go out on
the next start. While any of a tenant's rows waits out a retry time, its newer rows wait too and go out
with them, so a provider that answered 429 or 5xx is not called again for every commit. A tenant whose
settings are on but not usable (an invalid target id, or a token that cannot be decrypted) writes no rows.

## Settings

Host configuration, read on every use, so a change needs no restart:

| Key | Default | Meaning |
| --- | --- | --- |
| `Delivery:CdnPurge:DebounceMs` | 2000 | How long a row waits before it is sent, so repeated commits merge |
| `Delivery:CdnPurge:TimeoutSeconds` | 10 | Cut-off for one provider call |
| `Delivery:CdnPurge:MaxAttempts` | 5 | Attempts before the rows are dropped |
| `Delivery:CdnPurge:RetryBackoffSeconds` | 30 | Base delay after a failure, doubled each attempt, at most 1 hour |
| `Delivery:CdnPurge:MaxTagsPerFlush` | 500 | Cap on tags in one flush for one tenant |
| `Delivery:CdnPurge:PollSeconds` | 15 | How often an idle sender looks for rows |

Past `MaxTagsPerFlush` the flush drops the entry tags and keeps the type, sitemap and redirects
tags. If that is still over the cap, it sends one purge of the whole tenant tag. Each flush that
collapses logs one line.

A tenant's own settings are cached for 30 seconds, and saving them through the settings route
clears the cache on that node.

## What is recorded

One `WebhookDelivery` row per attempt, with `Event` of `CdnPurge`, the provider host (never a path),
the status, how long it took, the error text and the number of tags in the `X-Purge-Tag-Count`
header. Never the token and never the tag list. Logs carry neither.
