# Configuring email

barakoCMS sends email for registration, sign-in codes and workflow actions. Two things have to be
true before any of it is delivered: a provider module is registered, and it has credentials.

## The provider is a deployment decision, the credentials are not

The provider is a module, so it is chosen when the host is assembled:

```csharp
services.AddBarakoCMS(config, m => m.Add(new ResendEmailModule()));   // Resend HTTP API
services.AddBarakoCMS(config, m => m.Add(new SmtpEmailModule()));     // any SMTP relay
```

Without one, the core registers a mock that logs and delivers nothing. The admin says so, and the
test send refuses rather than reporting success.

`BarakoCMS.Email.Smtp` registers itself only once `Modules:Email.Smtp:Host` is set. Adding the
package and configuring nothing leaves whatever was sending before still sending, so an upgrade
cannot quietly hand email to a provider that has nowhere to send it.

The credentials are editable at **Settings, Email** by a SuperAdmin, and take effect on the next
send with no restart. That is the point: a process owner standing up an instance can get email
working without anybody editing a deployment.

## Where a value comes from

Each field is resolved independently, and what was stored in the admin beats what the deployment
configured:

| Field | Stored | Configured |
| --- | --- | --- |
| API key | Settings, Email | `Resend:ApiKey`, or the `RESEND_API_KEY` environment variable |
| From address | Settings, Email | `Resend:From` |

Those two fields are the whole of it, and the shape is Resend's. **SMTP credentials do not live
here**: host, port, user, password and TLS mode are read by `BarakoCMS.Email.Smtp` from its own
`Modules:Email.Smtp` section, which is a deployment decision rather than an admin one. The from
address does carry across, because it is the one field in this screen that is not about a
particular provider, and a sender stored here wins over the module's own `From`.

The consequence to know about: with SMTP registered, **the test-send button below refuses with "No
API key is set"**, because it checks for one before sending. Ordinary sends are unaffected.
Fixing that means making `IEmailSettingsProvider` provider-neutral, which is a change to the core
contract rather than to a module, so it is not done here.

Stored wins because it is the one a person set most recently, through the surface built for it.
Configuration is how a deployment with no database row yet is seeded, and it keeps working when
nothing is stored.

Per field, not all or nothing. Setting a From address in the admin does not switch off a configured
API key, because that cliff would stop email working the moment somebody filled in one box.

The screen shows where each value came from, so an operator does not have to set one and watch the
other win.

## The key is encrypted, and never comes back

The API key is stored encrypted with `ISecretProtector` (AES-GCM), so a database dump or a backup
does not hand over a working sending credential.

`GET /api/settings/email` says whether a key is set and where it came from. It does not return the
key, and there is no field in the response that could carry it. The admin form cannot prefill it
either, which is deliberate: a form that repopulated the box would put the secret in every browser
cache, every screen share and every proxy log.

The consequence to know about: **there is no way to read the key back**, from the API or the admin.
If you need it again, get it from the provider.

### Rotation makes stored credentials unreadable

The encryption key is derived from `Secrets:Key`, falling back to `JWT:Key`. Changing whichever is
in use makes every stored credential undecryptable, and there is no recovery beyond entering it
again. Set a dedicated `Secrets:Key` so it is not tied to the JWT signing key, and treat changing it
as a migration. `SECURITY.md` carries the same warning for `Mfa:Key`.

When a stored key will not decrypt, the log says so and the resolver falls back to configuration
rather than failing silently. The screen will show the API key as coming from the deployment, which
is the signal that it needs entering again.

## Credentials do not go in the general settings store

`POST /api/settings` refuses a key that looks like a credential (`apikey`, `password`, `secret`,
`token`, `credential`, `privatekey`). Everything in that store is held in plaintext and returned in
full by `GET /api/settings`, which is right for a feature flag and wrong for a sending credential.

## The test send

**Settings, Email, Send a test to myself** sends one message to the signed-in user's own address.

It goes to the caller and nowhere else. An endpoint that took a recipient would be a way to send
mail from this deployment's domain to any address somebody named.

It refuses, with the provider's own reason, when there is no provider, no key, or the provider
rejects the request. The key check is why this button does not work with the SMTP module, which has
no API key to find: see the note under "Where a value comes from". A test button that cannot fail is worse than no button: it moves the failure to
the first real invoice and tells the operator it already worked.

## Auditing

Changing email settings is recorded as `settings.email.changed` in the audit trail, with which
fields changed and never their values. It is a SuperAdmin action rather than an Admin one, because
redirecting where the system's mail comes from redirects every password reset and every verification
token in the deployment.
