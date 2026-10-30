# Social sign-in: provider setup, the code handoff and Facebook data deletion

`BarakoCMS.ExternalAuth` signs people in with Google, GitHub, Facebook, LinkedIn or any OpenID
Connect provider. This page covers what to register at each provider for a public site, how a
server-side frontend finishes a sign-in with a one-time code, and the Facebook data deletion URL.
Settings for the other providers and the account-linking rules are in
`BarakoCMS.ExternalAuth/README.md`.

Set `App:BaseUrl` to the public URL of the API first. Every redirect URL below is built from it, and
a provider rejects a redirect it has not been given.

## Provider setup

| Provider | Register this redirect URL | Settings |
|---|---|---|
| Google | `{App:BaseUrl}/api/auth/google/callback` | `Google:ClientId`, `Google:ClientSecret` |
| GitHub | `{App:BaseUrl}/api/auth/github/callback` | `GitHub:ClientId`, `GitHub:ClientSecret` |
| Facebook | `{App:BaseUrl}/api/auth/facebook/callback` | `Facebook:AppId`, `Facebook:AppSecret` |

A sign-in starts at `GET /api/auth/{provider}/start?club={handle}`.

**Google.** Create an OAuth client of type Web application. Add the redirect URL under Authorized
redirect URIs. Publish the consent screen to production before a public launch.

**GitHub.** An OAuth App has exactly one callback URL. Register one OAuth App for each callback URL
you serve (for example one for production and one for staging), each with its own client id and
secret. Reusing one app across two hosts fails at the second.

**Facebook.** Add the Facebook Login product, and put the redirect URL under Valid OAuth Redirect
URIs. The start route asks for the scope `email,public_profile` and nothing else. Both are available
without App Review, so the app can go live without it. Birthday and location are opt-in: set
`Facebook:RequestBirthdayAndLocation` to `true` to also ask for `user_birthday` and `user_location`.
Those two permissions need App Review, so leave the setting off until the app has passed it.

To switch the app from Development to Live mode, Facebook requires:

- a privacy policy URL (App settings, Basic),
- the data deletion callback URL: `{App:BaseUrl}/api/auth/facebook/data-deletion`
  (App settings, Advanced, or Facebook Login settings, Data Deletion Request URL).

Facebook does not say whether an address is verified, so `Facebook:TrustUnverifiedEmail` has to be
`true` for a first Facebook sign-in to match a local user by email. Read the README before turning it
on.

## The code handoff

By default a callback redirects the browser to `{App:BaseUrl}/auth/social#token=...&refresh=...&club=...`,
or to `#mfa_challenge=...` when the account has a second factor. A fragment never reaches a server, so
a frontend that renders on the server cannot read it. Set

```json
{ "ExternalAuth": { "CodeHandoff": true } }
```

and every callback (Google, GitHub, Facebook, LinkedIn, OpenID Connect, and the MFA case) redirects
to `{App:BaseUrl}/auth/social?code={code}` instead. The URL carries only the code. The default is
`false`, so existing frontends keep working.

The frontend then calls, anonymously:

```
POST /api/auth/social/exchange
Cookie: social_device=...
Content-Type: application/json

{ "code": "..." }
```

The answer is `200` with `{ "token", "refreshToken", "club" }`, or, when the account owes a second
factor, `{ "requiresMfa": true, "mfaChallengeToken", "club" }`, which is finished at
`POST /api/auth/mfa/verify` as before. Anything else is `400` with the same message, whatever the
reason.

A code:

- lives 60 seconds,
- works once. A second use, or the loser of two requests at the same moment, gets `400`,
- works only for the browser the sign-in started in. The start route sets the HttpOnly cookie
  `social_device` (ten minutes) when the handoff is on, and the exchange must present it. A
  frontend that calls from its own server forwards the browser's `Cookie` header, so the frontend
  and the API have to be reachable on the same site (a reverse proxy in front of both, or a shared
  parent domain). A code presented without the cookie, or with another one, is refused and is not
  used up,
- is stored as a SHA-256 hash. The tokens in the row are encrypted with a key derived from the code,
  so the table alone opens nothing, and the payload is cleared once the code is used.

Starting a sign-in stores nothing. A row exists only after a provider has vouched for a sign-in.
Expired rows are deleted in bounded batches at most once a minute, and no more than 5000 live codes
exist at once; past that a callback sends the person back to the login page to try again.

The change adds the tables `social_handoff_codes` and `facebook_deletion_requests`. Production runs
with `AutoCreate.CreateOnly`, so apply `migrations/5.5.0/1270-external-auth-social-handoff.sql`
before the deploy (`db-migrate` does it).

## Facebook data deletion

When a person removes the app from their Facebook settings, Facebook posts
`signed_request=<signature>.<payload>` as a form to `POST /api/auth/facebook/data-deletion`. The
route:

- checks the HMAC-SHA256 signature with `Facebook:AppSecret` (constant-time compare), and answers
  `400` for a missing, oversized (over 4096 characters), badly encoded, wrongly signed or
  other-algorithm request, removing nothing,
- removes that person's Facebook link (`oauth:facebook` and their Facebook id) at once,
- answers `200` with `{ "url": "{App:BaseUrl}/api/auth/facebook/data-deletion/status/{code}", "confirmation_code": "{code}" }`.

The code is 64 random hex characters. Only its SHA-256 is stored (as the document id), so the raw
code exists in Facebook's hands and nowhere else.

### When Facebook was the only way in

Another login means a non-empty password, or another linked provider identity. Magic link and email
code sign-in do not count: they work for any account with an address on file, so counting them
would leave almost no account as "Facebook only".

- With another login, the link is removed, the account stays, and the status is `completed`.
- With no other login, removing the link is the same as deleting the account, so the person gets
  the choice. The link is gone at once and the status is `awaiting_choice`, with `choiceExpiresAt`.
- `GET /api/auth/facebook/data-deletion/status/{code}` shows a small page with two buttons when the
  client sends `Accept: text/html`, and JSON otherwise. It never changes anything.
- `POST` to the same URL with `choice=delete_account` or `choice=keep_account` (form or JSON,
  anonymous) settles it. Keep gives `kept`. Delete erases the account and gives `completed`.
  Anything else is `400`.
- With no choice by `choiceExpiresAt`, an hourly sweep erases the account. The window is
  `Facebook:DeletionChoiceDays`, default `30`.
- The login state is checked again at choice time and sweep time. If the account gained a password
  or another identity, or linked Facebook again, nothing is erased and the status is `kept`.
- A choice after the request settled changes nothing and returns the settled status. Two choices at
  once settle once (optimistic concurrency on the request), and both callers see the winner.
  The sweep is safe to run twice or on two nodes for the same reason.
- A platform administrator, a SuperAdmin, or the last active admin of a tenant is never erased
  automatically. The status is `needs_admin` and the account is left alone.

Statuses: `completed`, `awaiting_choice`, `kept`, `needs_admin`.

### What erasure does

One transaction. The `User` row stays as a tombstone (username `erased-{id}`, email
`erased-{id}@erased.invalid`, no password, roles, groups or display name) so access tokens already
issued stop working through the session epoch and the unique username and email values are freed.

- Deleted: memberships, API keys, tenant account manager rows, social profile (Facebook birthday,
  location, photo), external identities, MFA secret, and email codes, magic link tokens and pending
  registrations for the account's address.
- Changed: the user id is removed from user group lists. Refresh tokens are revoked (rows stay
  revoked, they hold no plaintext token).
- Kept: revoked token ids, audit events that name the user (the audit chain is tamper-evident), and
  plain id references in other modules (`CreatedBy` on content, stations, share links, files,
  accounting entries, diagnostics client errors).
- Added: one `user.erased` audit entry with the user id and a reason, and no Facebook user id.

The social profile data is removed only when the account is erased. When the account has another
login, only the link goes.

### Rate limit and settings

The exchange route and the anonymous deletion routes share one policy, per client address:

| Key | Default | |
|---|---|---|
| `Social:RateLimit:PermitLimit` | `10` | Requests per window |
| `Social:RateLimit:WindowSeconds` | `60` | Window length |
| `Facebook:DeletionChoiceDays` | `30` | Days a person has to choose |

A value that is not a positive number uses the default.
