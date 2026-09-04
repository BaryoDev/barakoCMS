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
the session with it. That is the difference between a bug you fix and a credential you have to
revoke, and it is the whole reason for the change, but it is not immunity.

The cookie is scoped to `/api/auth/refresh`, so it is not attached to every API call, only to the one
route that consumes it.

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

**The API still returns the refresh token in the response body.** A cookie is a browser mechanism.
The generated clients, anything you build with the module packages, a mobile app, a script in CI:
none of those have a cookie jar you would want to rely on, and all of them read the token from the
response today.

Making the cookie a replacement rather than an addition would have broken every non-browser caller
in order to fix a browser-only problem. So `POST /api/auth/refresh` accepts the token in the body
**or** in the cookie, and prefers the body when both are present.

The security gain is not that the token stopped existing in the response. It is that the admin stops
**persisting** it. A page that holds a credential for a few milliseconds during sign-in is a much
smaller target than one that keeps it in storage for a week.

## What this needs from your deployment

The cookie is `SameSite=Lax`, which means the browser sends it when the admin and the API are the
same site.

- **Same origin, or same site with different paths.** Works with no configuration. The bundled
  playground serves the admin at `/barakocms` and the API at `/barakocms-api`, which is why it works
  there.
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
