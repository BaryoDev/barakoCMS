<div align="center">
  <h1>BarakoCMS.Email.Smtp</h1>
  <p><em>SMTP email provider for barakoCMS.</em></p>
</div>

---

Implements barakoCMS's `IEmailService` over any SMTP relay, so features that send email
(registration, passwordless OTP sign-in, MFA notices, workflow emails) deliver for real instead of
hitting the built-in mock.

If you self-host, you very likely already have SMTP credentials from your host, from Google
Workspace, from Amazon SES or from a corporate relay. This is the provider that uses them.

Sending is [MailKit](https://github.com/jstedfast/MailKit), not `System.Net.Mail.SmtpClient`, which
Microsoft's own documentation tells you not to use in new code.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Email.Smtp.SmtpEmailModule());
});
```

barakoCMS registers its mock email service with `TryAdd`, so this module's registration wins.

## Configure

Settings live in the module's own section, `Modules:Email.Smtp`:

```json
{
  "Modules": {
    "Email.Smtp": {
      "Host": "smtp.example.com",
      "Port": 587,
      "User": "postmaster@example.com",
      "Password": "…",
      "From": "MyApp <no-reply@example.com>",
      "Security": "StartTls"
    }
  }
}
```

As environment variables that is `Modules__Email.Smtp__Host` and so on: `__` stands in for the
separator, and the dot in the module name is part of the key rather than one.

The dot means the name is not a valid shell identifier, so `export Modules__Email.Smtp__Host=...`
fails in bash and zsh. It works anywhere the name is passed as data rather than declared: a compose
`environment:` list, a Kubernetes `env:` entry, or `env 'Modules__Email.Smtp__Host=smtp.example.com'
dotnet run`. For a local shell, user-secrets is the easier route.

Keep the password in user-secrets, an environment variable or a mounted secret, never in source.

| Setting | Description |
|---|---|
| `Host` | The relay. **No host means the module registers nothing**, see below. |
| `Port` | Submission port. Default `587`. |
| `User`, `Password` | Omit both for a relay that does not authenticate. |
| `From` | Sender. A from address stored in the admin at Settings, Email wins over this. |
| `Security` | `StartTls`, `SslOnConnect` or `None`. Unset picks by port. |

### It is inert until you configure it

With no `Host` the module registers no `IEmailService` at all, so whatever was there before still
sends: another provider module, or the mock. Adding the package to an existing deployment and
configuring nothing changes nothing.

The alternative would be worse than it sounds. A module that registered itself unconfigured would
take email over from a working provider and then fail every send, and it would read as the relay
being down rather than as the upgrade.

### The security default will not fall back to plaintext

Unset `Security` means implicit TLS on port 465 and STARTTLS on every other port. If the relay does
not offer STARTTLS, the send fails.

That is deliberate, and it is not what MailKit's own `Auto` does: `Auto` off port 465 resolves to
StartTlsWhenAvailable, which sends the password in the clear against a relay that stopped
advertising STARTTLS, and nothing anywhere says so. Plaintext is still available by asking for it
by name, `"Security": "None"`, for a relay on a network you already trust.

There is no setting that turns off certificate validation. If your relay's certificate does not
validate, fix the certificate or trust the CA on the host.

## What this module does not get from the admin

barakoCMS has an email settings screen at **Settings, Email**, and it holds an API key and a from
address. That shape was built for Resend, and it is what a provider gets from
`IEmailSettingsProvider`. SMTP needs a host, a port, a user, a password and a TLS mode, and none of
those fit in it.

So:

- **The from address does carry across.** A sender typed into the admin wins over
  `Modules:Email.Smtp:From`, because that field is not provider-specific and somebody set it there
  most recently.
- **The credentials do not.** Host, port, user and password are a deployment decision for this
  module, changed where the deployment is configured and picked up on the next send.
- **The test-send button refuses.** *Settings, Email, Send a test to myself* checks for an API key
  before it sends, so with SMTP registered it answers "No API key is set" even when SMTP is
  configured and working. Ordinary sends are unaffected. Making that button provider-neutral means
  changing `IEmailSettingsProvider`, which is a change to the core contract rather than a module.

## Requires

barakoCMS ≥ 4.0.0. Targets .NET 10.

## License

[MPL-2.0](LICENSE) © BaryoDev

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
