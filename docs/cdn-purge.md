# CDN purge client

Core has a client that purges a CDN by cache tag, and per-tenant settings that say which CDN and
with what credentials. Nothing calls the client yet. Wiring it to publish is a later slice (#561),
so setting these values today changes no behaviour.

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
never a value.

The default tenant has no row and gets a 400 on these routes. It reads host configuration:

```
Delivery:CdnPurge:Enabled      true to turn it on
Delivery:CdnPurge:Provider     cloudflare or fastly
Delivery:CdnPurge:TargetId     zone id or service id
Delivery:CdnPurge:ApiToken     the provider token
```

`Delivery:CdnPurge:BaseUrl:Cloudflare` and `:Fastly` override the provider's API root. They are host
configuration only, so a test can point the client at a stand-in.

## The two providers

| Provider | Call | Auth | Body | Tags per call |
| --- | --- | --- | --- | --- |
| Cloudflare | `POST https://api.cloudflare.com/client/v4/zones/{zoneId}/purge_cache` | `Authorization: Bearer <token>` | `{"tags":[...]}` | 100 |
| Fastly | `POST https://api.fastly.com/service/{serviceId}/purge` | `Fastly-Key: <token>` | `{"surrogate_keys":[...]}` | 256 |

The limits are the ones in the vendors' documentation: the
[Cloudflare purge by tag page](https://developers.cloudflare.com/cache/how-to/purge-cache/purge-by-tags/)
and the [Fastly purging reference](https://www.fastly.com/documentation/reference/api/purging/).
A longer list goes out in several calls. The first call that fails stops the rest. The calls go
through the guarded `ExternalApi` HTTP client.

## Result

`PurgeTagsAsync` returns `Succeeded`, `Status`, `Error`, `Retryable` and `RetryAfter`. `Retryable` is
true for a network failure, 429 and 5xx, and false for 400, 401, 403 and 404. A 429 carries the
provider's `Retry-After` when it sent one. The error text never contains the token, even when the
provider echoes it back. An empty tag list sends nothing and succeeds.

Tags are the ones `DeliveryCache.PurgeTags` returns. They are sent unchanged, so `:` and `%` in a
tag reach the provider as written.
