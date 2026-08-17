<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.FeatureFlags logo" />
  <h1>BarakoCMS.FeatureFlags</h1>
  <p><em>Ship it dark, turn it on for some people, then everyone.</em></p>
</div>

---

Create a flag, toggle it, and target it — by tenant, by user, or by percentage rollout. Flags are
global with per-tenant targeting, and every decision is made server-side.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.FeatureFlags.FeatureFlagsModule());
});

var app = builder.Build();
app.UseBarakoCMS();
```


## Endpoints

| Method & path | Purpose | Access |
|---|---|---|
| `GET    /api/feature-flags` | Flags as evaluated for the caller | Any signed-in user |
| `GET    /api/feature-flags/admin` | Every flag with its targeting rules | `Admin` / `SuperAdmin` |
| `POST   /api/feature-flags/admin` | Create or update a flag | `Admin` / `SuperAdmin` |
| `POST   /api/feature-flags/admin/{key}/toggle` | Flip a flag | `Admin` / `SuperAdmin` |
| `DELETE /api/feature-flags/admin/{key}` | Remove a flag | `Admin` / `SuperAdmin` |

`GET /api/feature-flags` returns decisions, not rules — the client never learns why it was included,
and cannot flip itself in by editing a response.

## A flag is not access control

Use a flag to decide whether a feature is *available*. Keep using roles to decide whether a caller
is *allowed*. A flag that hides an admin button still leaves the endpoint reachable.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 8. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
