# Where the admin keeps your session, and why

> The admin described here is barakoBrew, which now lives in its own repository
> ([BaryoDev/barakoBrew](https://github.com/BaryoDev/barakoBrew)). The API half, the refresh cookie
> and the endpoints that issue and read it, is still here.

Short version: the access token lives in memory and disappears on reload; the refresh token lives in
a cookie the page cannot read. If you sign in on a second tab and it pauses for a moment before
loading, that is this working as intended.

## What changed in 4.0

Before 4.0 the admin kept both tokens in `localStorage`.

`localStorage` is readable by any JavaScript running on the origin. That is not a flaw in
`localStorage`, it is what it is for. It becomes a problem when what you keep there is a credential.

Two credentials were kept there, and they are not equally dangerous:

| token | life | what it can do |
| --- | --- | --- |
| access token | 15 minutes | authorise requests until it expires |
| refresh token | 7 days, renewable | mint fresh access tokens, indefinitely |

The refresh token is the one that mattered. Stealing it is not stealing a session, it is stealing the
ability to keep making sessions. Rotation does not help: an attacker who holds it and keeps
refreshing rotates it along with you, and the honest owner is the one who gets logged out.

So a single cross-site scripting bug, or a single compromised npm dependency inside the admin's own
build, was a week of account takeover rather than fifteen minutes of nuisance.

## What it does now

**The refresh token is an httpOnly cookie.** The server sets it; the browser sends it back; the page
has no way to read it. The durable credential is never in reach of script, so an XSS cannot copy it
out and keep refreshing from somewhere else next week.

Be clear about what that does not buy you. Script running on the page can still call
`POST /api/auth/refresh`, and the browser will attach the cookie for it, exactly as it does for the
admin. So an active XSS can use your session for as long as it is running. What it cannot do is take
the session with it: a refresh that arrived with only the cookie answers with the new refresh token in
the cookie and an empty `refreshToken` in the body. That is the difference between a bug you fix and a credential you have to
revoke, and it is the whole reason for the change, but it is not immunity.

The cookie is scoped to `/api/auth/refresh` and `/api/auth/logout` (one cookie per path), so it is
not attached to every API call, only to the two routes that consume it. Logout reads it so a
console whose access token has expired can still sign out: with no usable bearer, `POST
/api/auth/logout` revokes every refresh token of the cookie's user when the cookie is live, and
revokes nothing for a spent, expired or unknown one, so an old copy cannot sign you out. It clears
the cookie and answers 200 either way, and 401 only when there is neither a bearer nor a cookie. `SameSite=Lax` keeps the cookie off a POST from another site, so another site cannot sign
you out.

**The server stores a hash of the refresh token, not the token.** A copy of the `refresh_tokens`
table does not hold working sessions. Rows written before this change hold the plain token; they
still refresh once, and the row that replaces them is hashed, so the last of them expires within
seven days and nobody is signed out by the upgrade.

**The access token is a variable in memory.** Not `localStorage`, not `sessionStorage`. It is gone
when the tab reloads, and the admin quietly asks for a new one using the cookie.

## What you will notice

Two things, both small, both a direct consequence of the above:

- **A reload or a new tab pauses briefly.** There is no token in memory yet, so the admin does one
  silent refresh before the first request. That round trip is the cost of not persisting anything.
- **Closing every tab ends the in-memory half.** The cookie still carries your session, so you are
  not signed out; the next visit refreshes and continues.

If you were signed in before upgrading, the old values are cleared from `localStorage` the first time
you sign out or your session ends, so an upgrade does not leave a week-long refresh token sitting in
storage for a later bug to find.

## What did not change, and why

**Sign-in still returns the refresh token in the response body**, and so does a refresh that was
sent the token in the body. A cookie is a browser mechanism.
The generated clients, anything you build with the module packages, a mobile app, a script in CI:
none of those have a cookie jar you would want to rely on, and all of them read the token from the
response today.

Making the cookie a replacement rather than an addition would have broken every non-browser caller
in order to fix a browser-only problem. So `POST /api/auth/refresh` accepts the token in the body
**or** in the cookie, and prefers the body when both are present.

The security gain is not that the token stopped existing in the response. It is that the admin stops
**persisting** it. A page that holds a credential for a few milliseconds during sign-in is a much
smaller target than one that keeps it in storage for a week.

## Sessions

Each sign-in is a session with its own id. The id is on the refresh row (`SessionId`) and in every
access token the session mints, as a `sid` claim. Rotation keeps it, so the id names the sign-in and
not one refresh token. A tenant switch copies `sid` from the token it was given, so a person signed
in on two tenants from one sign-in holds one session, not two.

The refresh row also records, for a later list of sessions:

- `SessionStartedAt`, `Device`, `SignInIp` and `SignInMethod`, set at sign-in and carried across
  rotation.
- `LastSeenAt` and `LastSeenIp`, moved on each refresh.

`SignInMethod` is `password`, `code`, `link` or `social:<provider>`, with `+mfa` added when a second
factor was used. The MFA challenge carries the first factor to verify, so a sign-in that goes
through it records both.

`Device` is built from the user agent as "Chrome on macOS", or "Unknown device" when neither the
browser nor the system is recognised. The raw header is never stored. Control characters are
stripped and the text is cut at 80 characters. The device id is cut at 128 characters when written.

`SignInIp` and `LastSeenIp` are the address the API sees. Behind a proxy that is the proxy's
address unless `ForwardedHeaders:KnownProxies` lists it. Set that if you want real client addresses
in the session record.

### Ending one session

Ending a session stores a revoke key `sid:<id>` that lasts as long as the session can, and revokes
the session's refresh row with reason `session_revoked`. The token check and the realtime hub look
up the token's `jti` and its `sid:` key in one query, and refuse the token if either is there. Like
the `jti` check, it fails closed when the store cannot be read. Other sessions of the same user are
not touched.

A refresh that loses a race to this answers 401 and revokes nothing else. It is not treated as
token reuse.

Logout is unchanged and does not end only the current session.

### Listing and ending sessions

| Route | Who | What |
|---|---|---|
| `GET /api/me/sessions` | the signed-in user | their live sessions, newest `lastSeenAt` first, paged |
| `DELETE /api/me/sessions/{id}` | the signed-in user | ends one of their sessions, 204, or 404 when it is not theirs |
| `POST /api/me/sessions/revoke-others` | the signed-in user | ends every session except the one the request came from, 204 |
| `GET /api/users/{id}/sessions` | a global SuperAdmin | the same list for any user |
| `DELETE /api/users/{id}/sessions/{sessionId}` | a global SuperAdmin | ends one of that user's sessions |
| `DELETE /api/users/{id}/sessions` | a global SuperAdmin | ends all of that user's sessions |

A live session is a refresh row that is not revoked, not expired and has a session id, so rows from
before sessions are left out until their first refresh. Each item holds `id`, `device`,
`signInMethod`, `signInIp`, `lastSeenIp`, `startedAt`, `lastSeenAt` and `current`. `current` is true
for the session whose token made the request, and is always false on the admin list. Lists take
`page` and `pageSize` and never return more than 100 items.

Ending the session the request came from signs the caller out: the answer is still 204 and the next
request is a 401, and the refresh cookie is cleared as logout does. The end routes share the `logout`
rate limit, 30 a minute per address. The two list routes use the global limiter.

Ending all sessions, from either route, also revokes the user's refresh tokens that carry no session
id, since a refresh of one would start a new session.

The admin routes are for a global SuperAdmin only, a role in the user's own `RoleIds` and not one
held through a tenant membership. A session spans tenants, so the list shows addresses from every
tenant the user signs in to, and a tenant administrator does not see them. A SuperAdmin may use them
on themselves. A tenant administrator's view of sessions is not built.

Every end writes `auth.session.revoked` with the actor as the caller, `targetType` `User`, `targetId`
the user, and the session id in `metadata.sessionId`. It holds no device text and no address. The
entry goes to the tenant the request came in on.

### API keys

`GET /api/api-keys` returns `lastUsedIp` beside `lastUsedAt` for each key. Both are written in one
patch, at most once every five minutes per key, so the address is at most five minutes stale. It is
null for a key not used since this was recorded. The usage endpoint does not return it.

### Rows from before sessions

A refresh row written before this change has no `SessionId`. Its next refresh gives the new row one,
and the access token minted by that refresh carries it.

### Erasing a user

Rotation clears `Device`, `SignInIp` and `LastSeenIp` on the row it retires. Erasing a user blanks
the same three fields on every row of that user, used rows included.

## Blocking an account

`POST /api/users/{userId}/block` and `/unblock` are for a global SuperAdmin. They refuse your own
account (400) and any user holding the SuperAdmin role, in their global roles or in a membership
(403). A user who does not exist is a 404 whose error reads "No account has that id.", so a console can tell it from a server with no block route, which answers 404 with no body. A block is global: it is separate from `LockoutUntil`, which
the failed-login counter sets and a password reset clears. Both are audited, as `auth.account.blocked`
and `auth.account.unblocked`. Repeating either changes nothing and is not audited twice.

Block and unblock take a per-user advisory lock inside their transaction, so two calls at once run
one after the other and only the call that changed `BlockedAt` writes the audit row.

Blocking stores `BlockedAt` and `BlockedBy` on the user, revokes every refresh row, and caches
the session epoch as the far future while `BlockedAt` is set, so every access token is refused on
the next request, a token minted in the same second as the block included. Blocking a user who is
already blocked revokes refresh rows again, because a sign-in can commit its row between the first
block's query and its commit, but it does not audit again.

A blocked user gets no token on any path. The token issuer reads the stored row, so password,
code, link, MFA completion, social sign-in, the OIDC id token grant, refresh and tenant switch all
refuse, and so does a step-up code. Each answers as it does for a wrong password or an unknown
account, with the same work. A blocked password attempt is audited as `auth.login.blocked` with
reason `account_blocked`. Code and link requests send no mail and store nothing. A challenge or code
issued before the block does not finish. An API key owned by the user is refused with 401, and works
again after unblock. A service principal key does not read its creator and keeps working.

Unblocking clears both fields, revokes every refresh row (any row alive then was minted during the
block), and sets `TokensValidFrom` to the next whole second. Sessions and tokens the block ended stay
ended, so the person signs in again. `FailedLoginAttempts` and `LockoutUntil` are left as they were.

What to expect from this:

- Other instances keep a cached epoch for up to 30 seconds (`SessionEpochService.CacheDuration`).
  After a block, an access token can still pass on another instance for that long. After an unblock,
  another instance can refuse a fresh sign-in for up to the same 30 seconds.
- The epoch check fails open when the database cannot be read. The token issuer reads the stored
  row, so new sign-ins, refreshes and tenant switches close at once even then.
- A sign-in in the same second as the unblock is refused by the epoch, because token times and the
  epoch are compared in whole seconds. The person signs in a second time.
- Blocking the last Admin of a tenant is allowed. A SuperAdmin can still manage that tenant.

`GET /api/users` returns `"blocked": true` on a blocked account and omits the field otherwise.

## What this needs from your deployment

The cookie is `SameSite=Lax`, which means the browser sends it when the admin and the API are the
same site.

- **Same origin, or same site with different paths.** Works with no configuration. One host
  serving the console at `/admin` and the API at `/api` needs nothing set: same origin, so the
  cookie is sent.
- **Different port, same host**, for example the admin on `:3000` and the API on `:5005` in local
  development. These are different origins but the same site, and `SameSite=Lax` is about the site,
  so the cookie is sent. It needs credentialed CORS to be useful, which the API already configures
  for `http://localhost:3000`. If you move the admin to another port, add that origin.
- **Genuinely different sites**, the admin and the API on separate domains. The cookie is not sent,
  and there is no fallback: the admin posts an empty refresh, gets nothing back, clears its
  in-memory token and sends you to the sign-in page. It does not read the refresh token out of the
  response body.

So if the two halves are on separate domains, put them behind one reverse proxy so they share an
origin. That is the same thing you would do for cookie auth on any other application.

`Secure` is set on every host except Development, rather than following the scheme of the request.
`Request.IsHttps` describes the hop that reached the process, not the one the browser made, so
behind a proxy that terminates TLS the request arrives as plain http and the cookie would have gone
out without `Secure` on exactly the deployment that needs it. Development is exempt because a
`Secure` cookie is not sent over http and every local stack would break with a symptom that looks
like "refresh is broken" rather than like a cookie policy.

## What this does not fix

Cross-site scripting is still bad. This limits the blast radius of one; it does not prevent one.

An XSS present **during** sign-in can still read the access token from memory and the refresh token
out of the login response as it arrives. What it can no longer do is arrive a week later, read
storage, and find a valid credential waiting.

The Content-Security-Policy, the dependency audit in CI, and the accessibility and lint gates all
exist to make that first XSS less likely. This assumes one happens anyway and asks what it costs.

Related: `docs/compliance-posture.md` for the wider picture, and `SECURITY.md` for reporting.
