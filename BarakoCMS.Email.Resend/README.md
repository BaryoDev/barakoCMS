<div align="center">
  <h1>BarakoCMS.Email.Resend</h1>
  <p><em>Resend email provider for barakoCMS.</em></p>
</div>

---

Implements barakoCMS's `IEmailService` using the [Resend](https://resend.com) HTTP API, so features
that send email (password-reset, passwordless OTP sign-in, workflow emails) deliver for real instead
of hitting the built-in mock.

## Enable it

```sh
dotnet add package BarakoCMS.Email.Resend
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Email.Resend`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Email.Resend.ResendEmailModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.

barakoCMS registers its mock email service with `TryAdd`, so this module's registration wins.

## Configure

| Setting | Description |
|---|---|
| `Resend:ApiKey` (or `RESEND_API_KEY` env) | Your Resend API key (`re_…`). Keep it in user-secrets / env, never in source. |
| `Resend:From` | Sender, e.g. `MyApp <no-reply@yourdomain.com>`. Defaults to Resend's shared test sender. |

To send to arbitrary recipients, verify your domain in the Resend dashboard and set `Resend:From`
to an address on it. (The shared `onboarding@resend.dev` sender only delivers to your own account
email.)

## Requires

barakoCMS ≥ 4.0.0. Targets .NET 10.

## License

[MPL-2.0](LICENSE) © BaryoDev

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
