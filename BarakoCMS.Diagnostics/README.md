<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Diagnostics logo" />
  <h1>BarakoCMS.Diagnostics</h1>
  <p><em>Collect browser errors from your frontend so you can find them later.</em></p>
</div>

---

Your app POSTs captured browser errors — message, stack, page, user — and this module deduplicates
them by fingerprint with an occurrence count, so a bug hit 4,000 times is one row that says 4,000
rather than 4,000 rows.

## Enable it

```sh
dotnet add package BarakoCMS.Diagnostics
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Diagnostics`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Diagnostics.DiagnosticsModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


## Endpoints

| Method & path | Purpose | Access |
|---|---|---|
| `POST /api/client-errors` | Report a batch of captured errors | Anonymous |
| `GET  /api/client-errors` | Browse what has been reported | `Admin` / `SuperAdmin` |
| `POST /api/client-errors/{id}/resolve` | Mark a fingerprint resolved | `Admin` / `SuperAdmin` |

Reporting is anonymous on purpose: the errors worth catching often happen before anyone signs in.
That makes it a spammable endpoint, so it runs under its own tighter `telemetry` rate-limit policy
rather than the global budget.

## Sending errors

The barakoCMS admin ships a reporter you can copy. Two rules matter if you write your own: send with
plain `fetch` rather than an HTTP client that retries or refreshes tokens (a reporter that can fail
by reporting is a loop), and cap sends per page session so a render loop cannot flood the API.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
