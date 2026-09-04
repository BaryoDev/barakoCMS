# BarakoCMS.Pwa

PWA install tracking for barakoCMS. Records when the app is run as an installed PWA (added to the
home screen), anonymously or tied to the signed-in user, so the admin can see adoption and **who**
installed it.

## Install

```sh
dotnet add package BarakoCMS.Pwa
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Pwa`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Pwa.PwaModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.

## Endpoints

| Method | Route | Who | Purpose |
| --- | --- | --- | --- |
| POST | `/api/pwa/report` | anyone (captures the signed-in user if present) | client reports display-mode / install on launch |
| GET | `/api/pwa/installs` | Admin / SuperAdmin | list of devices, who, platform, installed, first/last seen |

The client posts `{ deviceId, displayMode, platform, installed }`. Deduped per `deviceId` (repeat
launches bump `lastSeenAt` / `launchCount`). Records are stored globally; the reporting tenant is kept
as data. Pair it with `@baryodev/pwa-kit`'s reporter on the frontend.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
