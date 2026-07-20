# BarakoCMS.Pwa

PWA install tracking for barakoCMS. Records when the app is run as an installed PWA (added to the
home screen), anonymously or tied to the signed-in user, so the admin can see adoption and **who**
installed it.

## Install

```csharp
services.AddBarakoCMS(config, m => m.Add(new PwaModule()));
```

## Endpoints

| Method | Route | Who | Purpose |
| --- | --- | --- | --- |
| POST | `/api/pwa/report` | anyone (captures the signed-in user if present) | client reports display-mode / install on launch |
| GET | `/api/pwa/installs` | Admin / SuperAdmin | list of devices, who, platform, installed, first/last seen |

The client posts `{ deviceId, displayMode, platform, installed }`. Deduped per `deviceId` (repeat
launches bump `lastSeenAt` / `launchCount`). Records are stored globally; the reporting tenant is kept
as data. Pair it with `@baryodev/pwa-kit`'s reporter on the frontend.
