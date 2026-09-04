# BarakoCMS.Analytics.Umami

Umami web-analytics for the barakoCMS admin. The module proxies a self-hosted [Umami](https://umami.is)
instance behind admin-only endpoints, so the CMS admin can show visitors, top pages, referrers and
countries — and register new tracked sites — without ever exposing Umami credentials to the browser.

![Analytics page in the barakoCMS admin](https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/BarakoCMS.Analytics.Umami/assets/analytics.png)

## Install

```sh
dotnet add package BarakoCMS.Analytics.Umami
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Analytics.Umami`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Analytics.Umami.UmamiAnalyticsModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.

(The `BarakoCMS.Suite` image already references it.)

## Configure

Bind the `Umami` section (env vars shown):

| Key | Env | Notes |
| --- | --- | --- |
| `Umami:Enabled` | `Umami__Enabled` | `true` to turn the integration on |
| `Umami:BaseUrl` | `Umami__BaseUrl` | Server-to-server URL, e.g. `http://umami:3000` |
| `Umami:Username` | `Umami__Username` | Umami account used to read stats |
| `Umami:Password` | `Umami__Password` | Kept server-side; never sent to the browser |
| `Umami:PublicUrl` | `Umami__PublicUrl` | Public origin the tracking script is served from (for the copy-paste snippet); defaults to `BaseUrl` |

When disabled or unconfigured the module stays inert: `GET /api/analytics/websites` returns
`{ configured: false }` and the admin shows a "connect Umami" hint instead of an error.

## Endpoints (Admin / SuperAdmin only)

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/analytics/websites` | Sites Umami tracks (for the picker) |
| POST | `/api/analytics/websites` | Register a site; returns the tracking snippet |
| GET | `/api/analytics/{websiteId}/summary?range=7d` | Headline counters |
| GET | `/api/analytics/{websiteId}/series?range=7d` | Pageviews/sessions over time |
| GET | `/api/analytics/{websiteId}/metric?type=url&range=7d` | Top-N breakdown (`url`,`referrer`,`country`,`browser`,`os`,`device`) |

`range` is one of `24h`, `7d` (default), `30d`, `90d`. The module persists nothing — every read is
live from Umami — so it registers no Marten documents.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
