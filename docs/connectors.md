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

## Secrets

`secrets` is write only. Each value is encrypted under `Connectors:Key` (see
[SECURITY.md](../SECURITY.md)) and stored in its own document, and no endpoint returns one: a
response carries `secretKeys`, the names of the secrets that are set.

On an update, a secret that is left out is kept and one sent as an empty string is removed. Two
edits do not keep a secret that is left out, because whoever makes the edit cannot read the stored
value and it would go to a host nobody entered it for:

- `baseUrl` moving to another scheme, host or port needs every stored secret entered again or
  cleared in the same request.
- `settings.TokenUrl` moving to another scheme, host or port needs `ClientSecret` entered again or
  cleared in the same request.

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
address guard (no loopback, link-local or private address), redirects are not followed, and the
timeouts are the same. At most 64 KB of the answer is read.

**Caching.** A token is kept in memory, per API instance, under the tenant, the connector and the
connector's `updatedAt`. It is reused until 30 seconds before `expires_in` runs out, and for an
hour at most. An answer with no usable `expires_in` is kept for one minute. Editing a connector
gives it a new `updatedAt`, so the next call asks for a new token. At most 256 tokens are held; past
that the one closest to expiry is dropped. A token is never written to the database, a log, a
workflow run or a response.

**A 401 from the provider.** When the provider answers 401 to a cached token, the sender drops it,
asks for one new token and sends once more. If that is refused too, the 401 is the result. A 401 to
a token fetched for that same call is not repeated.

**When the token endpoint does not grant a token.** Nothing is sent to the provider, and the
failure is reported the same way a failed call is: a sentence in the workflow run or the test
result. The sentence names the token endpoint's host and the status code. It names the OAuth
`error` code only when it is one of the standard ones (`invalid_client`, `invalid_scope` and so
on), and never quotes the body, since an error body can repeat what was sent. A collection sync
records only that the credentials could not be attached and says to test the connector, as it does
for every other `auth`.

**Limits.** This is the client credentials grant only. A provider that wants a signed JWT assertion,
a refresh token or a user's consent is not covered. Each API instance holds its own token, so N
instances make N token requests per lifetime. Two calls arriving together with no token cached each
ask for one.
