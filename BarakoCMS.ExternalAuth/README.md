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

```sh
dotnet add package BarakoCMS.ExternalAuth
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=ExternalAuth`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.ExternalAuth.ExternalAuthModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


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
  claim an address at a provider take over the matching account. Take **0.4.0 or later**: this was
  the documented intent from the start and the enforcement was missing, so Google, LinkedIn and
  Facebook never read a verification flag, and GitHub read one only for accounts whose address was
  private on their profile.

  What each provider is asked for now:

  | Provider | Source of truth | Behaviour |
  | --- | --- | --- |
  | Google | `email_verified` on the OIDC userinfo response | Refused when absent or false |
  | LinkedIn | `email_verified` on `/userinfo` | Refused when absent or false |
  | GitHub | `verified` on `/user/emails` | Only the verified primary is used; the unflagged profile email is ignored |
  | Facebook | none exists | Refused unless `Facebook:TrustUnverifiedEmail` is set |

- **Facebook is opt-in.** The Graph API exposes no per-field verification flag, so there is nothing
  to check and no honest way to claim the address is verified. Setting
  `Facebook:TrustUnverifiedEmail` to `true` says you have decided Facebook's own verification is
  good enough for your deployment, and accepts that a Facebook account asserting an address becomes
  a login for the local account holding it. It defaults to off, which refuses the sign-in.
- If the account has MFA enrolled, the provider callback issues an MFA challenge rather than a
  session token. Take **0.1.6 or later**: earlier versions minted a token directly, so a
  provider-account takeover skipped the second factor entirely.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
