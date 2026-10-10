# Connectors

A connector is a third party this instance can call: a base URL, how to authenticate, and the
credentials. Request definitions and collection syncs name a connector by its slug, so the
credential is entered once and nothing that composes a request ever holds it.

## Managing them

`GET /api/connectors` lists and `GET /api/connectors/{slug}` reads one, both behind
`view_connectors`. `POST /api/connectors` creates, `PUT /api/connectors/{slug}` edits,
`DELETE /api/connectors/{slug}` removes and `POST /api/connectors/{slug}/test` sends one probe to
`probePath` with the credentials attached, all four behind `manage_connectors`. Admin and
SuperAdmin hold both by default. Every write and every test is in the audit log, with the names of
the secrets held and never a value.

## Authentication

`auth` is one of:

| `auth` | `settings` | `secrets` | What is sent |
| --- | --- | --- | --- |
| `None` | | | Nothing. |
| `Basic` | `Username` | `Password` | `Authorization: Basic`. |
| `BearerToken` | | `Token` | `Authorization: Bearer` with the stored token. |
| `ApiKeyHeader` | `HeaderName` | `ApiKey` | The key in the named header. |
| `OAuth2ClientCredentials` | `TokenUrl`, `ClientId`, optional `Scope`, `Audience`, `ClientAuth` | `ClientSecret` | `Authorization: Bearer` with a token fetched from `TokenUrl`. |

Setting and secret names are case sensitive. A connector missing a setting or a secret its `auth`
needs is saved as it is, and a send or a test says which one is missing.

`settings` are stored and returned as plain text, so a setting whose name reads as a credential
(it holds a word such as `token`, `password`, `secret`, `apikey` or `authorization`) is refused
with a 400 that points at `secrets`. The settings in the table above are accepted, `TokenUrl`
included.

## Secrets

`secrets` is write only. Each value is encrypted under `Connectors:Key` (see
[SECURITY.md](../SECURITY.md)) and stored in its own document, and no endpoint returns one: a
response carries `secretKeys`, the names of the secrets that are set.

On an update, a secret that is left out is kept and one sent as an empty string is removed. Two
edits do not keep a secret that is left out, because whoever makes the edit cannot read the stored
value and it would go somewhere nobody entered it for:

- `baseUrl` moving to another scheme, host or port needs every stored secret entered again or
  cleared in the same request.
- `settings.TokenUrl` being new, or changing at all, needs `ClientSecret` entered again or cleared
  in the same request. The whole URL is compared, path included, since a token endpoint is one
  exact address.

Testing a connector writes only its last test time and result, so a test that is still out when an
update lands does not undo the update.

## OAuth 2.0 client credentials

```json
{
  "name": "Accounting",
  "slug": "accounting",
  "baseUrl": "https://api.provider.example",
  "auth": "OAuth2ClientCredentials",
  "settings": {
    "TokenUrl": "https://identity.provider.example/connect/token",
    "ClientId": "the-client-id",
    "Scope": "invoices.read invoices.write"
  },
  "secrets": { "ClientSecret": "the-client-secret" }
}
```

Before a call, the sender posts `grant_type=client_credentials` to `TokenUrl`, with `scope` and
`audience` when those settings are present, and attaches the `access_token` it gets back as a
Bearer header. The client id and secret go in an HTTP Basic header by default, each form encoded
first as RFC 6749 section 2.3.1 says. A provider that wants them in the form body instead gets
`"ClientAuth": "Body"`.

The token request uses the same outbound client as the call itself, so `TokenUrl` meets the same
address guard (no loopback, link-local or private address) and redirects are not followed. That
client's timeouts end when the response headers arrive, so the token request has a deadline of its
own: 30 seconds from the first byte sent to the last byte of the answer read, after which it is
reported as timed out. At most 64 KB of the answer is read.

**Caching.** A token is kept in memory, per API instance, under the tenant, the connector and the
connector's `updatedAt`. It is reused until 30 seconds before `expires_in` runs out, and for an
hour at most. An answer with no usable `expires_in` is kept for one minute. Editing a connector
gives it a new `updatedAt`, so the next call asks for a new token. One tenant holds at most 32
tokens; past that, that tenant's token closest to expiry is dropped, so a tenant with many
connectors does not push another tenant's tokens out. The instance holds at most 256 across all
tenants, and past that the one closest to expiry is dropped whoever it belongs to. A token is never
written to the database, a log, a workflow run or a response.

**A 401 from the provider.** When the provider answers 401 to a cached token that is at least a
minute old, the sender drops it, asks for one new token and sends once more. If that is refused
too, the 401 is the result. A 401 to a token fetched for that same call, or to one granted less
than a minute ago, is not repeated, so a provider that answers 401 for a reason a token cannot fix
costs at most one extra token request a minute.

**When the token endpoint does not grant a token.** Nothing is sent to the provider, and the
failure is reported the same way a failed call is: a sentence in the workflow run or the test
result. The sentence names the token endpoint's host and the status code. It names the OAuth
`error` code only when it is one of the standard ones (`invalid_client`, `invalid_scope` and so
on), and never quotes the body, since an error body can repeat what was sent. A collection sync
records only that the credentials could not be attached and says to test the connector, as it does
for every other `auth`. A failed grant is not remembered: the next call asks the token endpoint
again.

**Limits.** This is the client credentials grant only. A provider that wants a signed JWT assertion,
a refresh token or a user's consent is not covered. Each API instance holds its own token, so N
instances make N token requests per lifetime. Two calls arriving together with no token cached each
ask for one.

## The delivery log

Every send a workflow's `Request` action makes through a connector leaves one row, the way a
webhook delivery does. `GET /api/connector-deliveries` lists them newest first, paginated, behind
`view_workflow_runs`, the capability the webhook delivery list uses. Filters:

- `connector`: a connector's slug.
- `requestSlug`: a request definition's slug.
- `workflowId` and `runId`.
- `status`: `2xx`, `3xx`, `4xx`, `5xx`, or `failed` for a row with no response. An unknown value
  is a 400.

**One row is one attempt at the action**, not one HTTP request. A send that met a 401, was granted
a new token and went once more is one row whose `requestsSent` is 2, holding the last answer. A
request refused before anything went out (the composer refused it, or the credentials could not be
attached) is a row with `requestsSent` 0 and the reason in `error`. The token request has no row.
`requestsSent` counts what the sender sent, not the outbound client's own retries of a failed
connection.

Each row holds the workflow id, the run id, the trigger event and the attempt number when the
runner made the call, the connector's id and slug, the request's slug, the method, the URL cut to
scheme, host and port, the request headers, the response status, the response body, the duration
and `error`, which also says when a response arrived and the request's success rule was not met.

**What a row never holds.** The request body is not stored, and the URL is cut to scheme, host
and port. A request header keeps its name and has `[redacted]` for a value unless its value is what
the request definition composed, its name does not read as a credential, and it quotes none of the
values that were redacted. So the header the connector's credential went out in is redacted
whatever it is called. A name reads as a credential when the workflow parameter classifier says so
(with or without its `-` and `_`), when it contains `auth`, `cookie` or `signature`, or when `key`
stands in it as a whole word, as in `X-Functions-Key` or `Ocp-Apim-Subscription-Key`. An
idempotency key is not one.

The response body is the first 4096 bytes, with these taken out of it wherever they appear:

- every redacted header value, the token without its scheme, and both halves of a Basic pair;
- the value of every query parameter of the URL whose name reads as a credential, as written and
  decoded, and the URL's user info;
- the value of every field of the request body whose name reads as a credential. A value that is
  only inside an object with such a name, under a field of its own with an ordinary name, needs six
  characters to count.

Only an exact copy is found, and a credential written into the path of the URL has no name to be
found by, so a provider that quotes the path puts it in the stored body. That is why reading
`responseBody` needs `view_webhook_response_bodies` on top of `view_workflow_runs`, as it does for
a webhook delivery.

`requestHeaders` needs `view_webhook_response_bodies` too, and is `null` without it. A header an
operator wrote under an ordinary name is stored with its value, and reading it where it is
configured needs `manage_requests`, which this list does not ask for.

**Retention** is the webhook delivery sweep in [webhooks.md](webhooks.md): the rows are the same
document, so `Webhooks:DeliveryLogRetentionDays` removes them and
`Webhooks:ResponseBodyRetentionHours` clears their response bodies.

The rows are bounded by that sweep and by nothing else: `Webhooks:DeliveryLogRetentionDays` of
zero or less keeps them forever. There is no setting that stops response bodies being stored, as
there is none for webhooks.

**If the row cannot be written**, the send's result stands and a warning is logged with the
connector and request slugs. The write gets five seconds and a database session of its own. A send
the caller cancelled leaves no row. A send that throws before a request goes out (the database
failing while the credential is read, say) leaves a row naming the exception's type, and still
throws.

**Not recorded.** A connector test and a collection sync's fetch leave no row: a test's result is
on the connector and in the audit log, and a sync keeps its own last result. A host that registers
its own `IConnectorSender` gets no rows either.

**Inside a Conditional.** A `Request` or `Webhook` action that is a child of a `Conditional` is
handed the trigger and not the run, so its row has no `runId`, an all-zero `workflowId` and
attempt 1, and the `workflowId` and `runId` filters do not find it. `connector` and `requestSlug`
do.

**Rolling back.** A release before this one does not know the connector fields. If it runs the
retention sweep over these rows (after a rollback, or beside this release during a rolling
deploy), clearing a response body stores the row again without them, and the row then reads as a
webhook delivery in `GET /api/webhook-deliveries`.
