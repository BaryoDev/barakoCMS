<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.DeviceTrust logo" />
  <h1>BarakoCMS.DeviceTrust</h1>
  <p><em>Know which devices are signed in, and require approval for new ones.</em></p>
</div>

---

Records the device behind each sign-in, binds a session to the device it was issued to, and can
require OTP approval before a device it has never seen is allowed in.

The point is that a stolen token stops being enough on its own: it also has to be presented from the
device it was minted for.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.DeviceTrust.DeviceTrustModule());
});

var app = builder.Build();
app.UseBarakoCMS();
```


## Endpoints

| Method & path | Purpose |
|---|---|
| `GET  /api/devices` | The signed-in user's own devices |
| `POST /api/devices/{id}/revoke` | Sign a device out and refuse its tokens |

A user only ever sees and revokes their own devices.

## Notes

Revoking a device invalidates its refresh token immediately. An access token already issued stays
valid until it expires, so a revoked device can linger for the remainder of that window rather than
being cut off mid-request.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
