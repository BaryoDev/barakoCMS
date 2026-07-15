<div align="center">
  <h1>BarakoCMS.Email.Resend</h1>
  <p><em>Resend email provider for barakoCMS.</em></p>
</div>

---

Implements barakoCMS's `IEmailService` using the [Resend](https://resend.com) HTTP API, so features
that send email (password-reset, passwordless OTP sign-in, workflow emails) deliver for real instead
of hitting the built-in mock.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Email.Resend.ResendEmailModule());
});
```

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

barakoCMS ≥ 2.2.0. Targets .NET 8.

## License

[MPL-2.0](LICENSE) © BaryoDev
