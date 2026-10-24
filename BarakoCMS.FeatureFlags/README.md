<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.FeatureFlags logo" />
  <h1>BarakoCMS.FeatureFlags</h1>
  <p><em>Ship it dark, turn it on for some people, then everyone.</em></p>
</div>

---

Create a flag, toggle it, and target it — by tenant, by user, or by percentage rollout. Flags are
global with per-tenant targeting, and every decision is made server-side.

## Enable it

```sh
dotnet add package BarakoCMS.FeatureFlags
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=FeatureFlags`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.FeatureFlags.FeatureFlagsModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


## Endpoints

| Method & path | Purpose | Access |
|---|---|---|
| `GET    /api/feature-flags` | Flags as evaluated for the caller (superseded by OFREP, below) | Anyone (flags limited by visibility level) |
| `POST   /ofrep/v1/evaluate/flags[/{key}]` | OpenFeature evaluation (see below) | Anyone (flags limited by visibility level) |
| `GET    /api/feature-flags/admin` | Every flag with its targeting rules | `Admin` / `SuperAdmin` held as a global role |
| `POST   /api/feature-flags/admin` | Create or update a flag | `Admin` / `SuperAdmin` held as a global role |
| `POST   /api/feature-flags/admin/{key}/toggle` | Flip a flag | `Admin` / `SuperAdmin` held as a global role |
| `DELETE /api/feature-flags/admin/{key}` | Remove a flag | `Admin` / `SuperAdmin` held as a global role |

Flags are global and their targeting names tenants, so the admin routes answer 403 to an Admin whose
role comes only from a tenant membership.

`GET /api/feature-flags` returns decisions, not rules — the client never learns why it was included,
and cannot flip itself in by editing a response.

## OpenFeature (OFREP)

The module speaks the [OpenFeature Remote Evaluation Protocol](https://openfeature.dev/specification/appendix-c),
so an OpenFeature SDK with its stock OFREP provider reads these flags with no client of ours.
Give the provider the API's origin, `https://your-api`, as its base URL. Providers append the
protocol's own path to the host, which is where the routes are:

| Method & path | Purpose | Access |
|---|---|---|
| `POST /ofrep/v1/evaluate/flags/{key}` | One flag, evaluated for the caller | Anyone (flags limited by visibility level) |
| `POST /ofrep/v1/evaluate/flags` | Every flag the caller may see, with an `ETag`; a matching `If-None-Match` answers 304 | Anyone (flags limited by visibility level) |

The subset covered, stated rather than implied:

- **Boolean flags only.** `value` is `true` or `false`, `variant` is `on` or `off`. A string, number or
  object request for a key answers with the boolean, which an SDK reports as a type mismatch.
- **Reason:** always `STATIC`. The protocol's other reasons (`TARGETING_MATCH`, `SPLIT`,
  `DISABLED`) would say why a caller got its value, and this module returns decisions, not rules.
- **Errors:** `FLAG_NOT_FOUND` with 404, for a key that does not exist and, to an anonymous caller,
  for a private one, so the key is not disclosed.
- **Context:** only `targetingKey` is read, only from an anonymous caller, and only to choose the
  rollout bucket. The tenant and the user always come from the request. A context naming another
  tenant or user changes nothing, so it cannot be used to read someone else's flags.
- **Not covered:** context-based targeting rules, flag metadata, and the provider-side cache
  configuration endpoint.

`GET /api/feature-flags` keeps answering through 5.x. It is superseded by the routes above, and
every response says so with `Link: </ofrep/v1/evaluate/flags>; rel="successor-version"`.

## Public flags

A flag has a visibility level: `Public` (anyone), `SignedIn` (any signed-in caller) or `Admin` (callers
who can manage feature flags). `GET /api/feature-flags` and the OFREP routes return only the flags the
caller's level allows. Everything else is absent from the response, not returned as `false`, and an
OFREP single read of a flag the caller cannot see answers exactly like a missing key.

```http
POST /api/feature-flags/admin
{ "key": "new-checkout", "enabled": true, "visibility": "Public" }
```

`isPublic` still works: true means `Public`, false keeps the flag's current level (`SignedIn` if it was
Public). A flag saved with neither field is `Admin` only. Flags stored before levels existed read as
`Public` when `isPublic` was true and `SignedIn` otherwise, so an upgrade changes what nobody sees.
The admin list and save response return both `visibility` and `isPublic`.

## A flag is not access control

Use a flag to decide whether a feature is *available*. Keep using roles to decide whether a caller
is *allowed*. A flag that hides an admin button still leaves the endpoint reachable.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under [MIT](LICENSE).

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
