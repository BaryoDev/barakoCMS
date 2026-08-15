<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.ExternalAuth logo" />
  <h1>BarakoCMS.ExternalAuth</h1>
  <p><em>Continue with Google, GitHub, Facebook or LinkedIn.</em></p>
</div>

---

OAuth sign-in for barakoCMS. A user arriving through a provider is matched to a global user by
**verified** email and issued exactly the same tenant-scoped, device-bound token as the built-in
flows — social sign-in is another way in, not a second, weaker way in.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.ExternalAuth.ExternalAuthModule());
});

var app = builder.Build();
app.UseBarakoCMS();
```


## Endpoints

| Method & path | Purpose |
|---|---|
| `GET /api/auth/providers` | Which providers are configured, for rendering buttons |
| `GET /api/auth/{provider}/start` | Begin the OAuth handshake |
| `GET /api/auth/{provider}/callback` | Provider redirect target |
| `GET /api/me/profile` | Profile details captured from the provider |

`{provider}` is `google`, `github`, `facebook` or `linkedin`. Only configured providers are
advertised or accepted.

## Configuration

```json
{
  "ExternalAuth": {
    "Google": { "ClientId": "...", "ClientSecret": "..." },
    "GitHub": { "ClientId": "...", "ClientSecret": "..." }
  }
}
```

Omit a provider to leave it disabled.

## Security notes

- Matching is on a **verified** email only. Matching on an unverified one would let anyone who can
  claim an address at a provider take over the matching account.
- If the account has MFA enrolled, the provider callback issues an MFA challenge rather than a
  session token. Take **0.1.6 or later**: earlier versions minted a token directly, so a
  provider-account takeover skipped the second factor entirely.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 8. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
