<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.DeviceTrust logo" />
  <h1>BarakoCMS.DeviceTrust</h1>
  <p><em>Know which devices are signed in, and require approval for new ones.</em></p>
</div>

---

Records the device behind a sign-in completed with an email code, an authenticator code or a social
provider, binds a session to the device it was issued to, and can require OTP approval before a
device it has never seen is allowed in.

With `DeviceTrust:Enforce` on, a token bound to a device is refused unless the request also names
that device and the device is still trusted. Enforcement is off by default. With it off, a
password-only sign-in adds no device, and no sign-in or request is checked against its device. See
[Configuration](#configuration).

## Enable it

```sh
dotnet add package BarakoCMS.DeviceTrust
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=DeviceTrust`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.DeviceTrust.DeviceTrustModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


## Endpoints

| Method & path | Purpose |
|---|---|
| `GET  /api/devices` | The signed-in user's own devices |
| `POST /api/devices/{id}/revoke` | Stop the device's refresh tokens. Its access tokens are refused only with `Enforce` on |

A user only ever sees and revokes their own devices.

## Configuration

```json
{
  "DeviceTrust": { "Enforce": true }
}
```

`DeviceTrust:Enforce` defaults to `false`.

A client names its device by sending an id of its own choosing in the `X-Device-Id` header, the
same value on every request. A sign-in completed with an email code, an authenticator code or a
social provider records the device named in that header as trusted and puts its id in the token as
the `did` claim. A password sign-in from a device already trusted carries the claim too. A sign-in
that sends no header records nothing and gets a token with no `did` claim.

A refresh token keeps the `X-Device-Id` sent at sign-in, trusted or not, and a token refreshed from
it carries that id as `did`.

An account with an authenticator enrolled is asked for its code after the password in either mode,
and the rows below about a password sign-in do not apply to it.

| | `Enforce` off (the default) | `Enforce` on |
|---|---|---|
| Password sign-in from a device that is not trusted | Allowed, and the device is not recorded | No token is issued. A code is emailed, and signing in with it trusts the device named in the header |
| A request whose token has a `did` claim | Not checked | 401 unless `X-Device-Id` equals the claim and the device is still trusted |
| A request whose token has no `did` claim | Not checked | Not checked |

## Notes

Revoking a device invalidates its refresh token immediately. With `Enforce` off, an access token
already issued stays valid until it expires, so a revoked device can linger for the remainder of
that window rather than being cut off mid-request. With `Enforce` on, a revoked device's next
request is refused.

Turning `Enforce` on ends some sessions. A session that began with a password on a device that was
never trusted, and that sent `X-Device-Id`, gets a `did` claim at its next refresh. That device is
not trusted, so its requests answer 401 until the user signs in again and approves the device.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
