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
- removes that person's Facebook link (`oauth:facebook` and their Facebook id), and no other link,
  user or profile,
- answers `200` with `{ "url": "{App:BaseUrl}/api/auth/facebook/data-deletion/status/{code}", "confirmation_code": "{code}" }`.

`GET /api/auth/facebook/data-deletion/status/{code}` is anonymous and answers
`{ "confirmationCode", "status": "completed", "requestedAt" }`, or `404` for a code it did not issue.
The record holds no Facebook user id. After the link is removed, the person's local account stays,
and their next Facebook sign-in matches by verified email again. Removing the account is a separate
administrator action.
