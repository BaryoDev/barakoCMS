# Where the admin keeps your session, and why

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
has no way to read it. An XSS that lands after you have signed in finds nothing to take, because the
durable credential was never in reach of script.

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
- **Different origins entirely**, for example the admin on `:3000` and the API on `:5005` in local
  development. The cookie is not sent. The admin falls back to nothing, so it will use the response
  body path, and you get the pre-4.0 behaviour rather than an error.

If you want the cookie protection and you are running the two on separate hosts, put them behind one
reverse proxy so they share an origin. That is the same thing you would do for cookie auth on any
other application, and it is worth doing.

`Secure` follows the request rather than being hardcoded on, because a `Secure` cookie is not sent
over plain http and hardcoding it would break every http development stack with a symptom that looks
like "refresh is broken" rather than like a cookie policy. Production is https, which is where it
counts.

## What this does not fix

Cross-site scripting is still bad. This limits the blast radius of one; it does not prevent one.

An XSS present **during** sign-in can still read the access token from memory and the refresh token
out of the login response as it arrives. What it can no longer do is arrive a week later, read
storage, and find a valid credential waiting.

The Content-Security-Policy, the dependency audit in CI, and the accessibility and lint gates all
exist to make that first XSS less likely. This assumes one happens anyway and asks what it costs.

Related: `docs/compliance-posture.md` for the wider picture, and `SECURITY.md` for reporting.
