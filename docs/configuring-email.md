# Configuring email

barakoCMS sends email for registration, sign-in codes and workflow actions. Two things have to be
true before any of it is delivered: a provider module is registered, and it has credentials.

## The provider is a deployment decision, the credentials are not

The provider is a module, so it is chosen when the host is assembled. The package reference plus a
restart is the install:

```sh
dotnet add package BarakoCMS.Email.Resend   # Resend HTTP API
dotnet add package BarakoCMS.Email.Smtp     # any SMTP relay
```

`AddBarakoCMS(builder.Configuration)` finds the referenced module, and `BarakoCMS:Modules:Enabled`
decides whether it runs; see `MODULES.md`.

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

`POST /api/settings` refuses a key that looks like a credential: one that contains `secret`,
`password`, `passwd`, `pwd`, `token`, `apikey`, `api_key`, `credential`, `privatekey`,
`private_key`, `accesskey` or `access_key`, in any casing. It is the same rule that decides which
workflow action parameters are encrypted and left out of responses. Everything in that store is held
in plaintext and returned in full by `GET /api/settings`, which is right for a feature flag and
wrong for a sending credential.

A setting stored under such a key before the key was refused is still returned by
`GET /api/settings` and still read by the API. Its value can no longer be changed through
`POST /api/settings`, and it can be cleared there: save the key with an empty value (`"value": ""`)
and the stored value is emptied. The row stays, since no route deletes a setting. An empty value for
a key that has no row is refused like any other.

## The test send

**Settings, Email, Send a test to myself** sends one message to the signed-in user's own address.

It goes to the caller and nowhere else. An endpoint that took a recipient would be a way to send
mail from this deployment's domain to any address somebody named.

It refuses, with the provider's own reason, when there is no provider, no key, or the provider
rejects the request. The key check is why this button does not work with the SMTP module, which has
no API key to find: see the note under "Where a value comes from". A test button that cannot fail is worse than no button: it moves the failure to
the first real invoice and tells the operator it already worked.

## Values in a workflow email

Every provider sends the body as HTML. So in an `Email` workflow action, a `{{...}}` value in
`Body` is HTML-encoded: a field holding `<b>hi</b>` shows those characters in the message rather
than bold text. The template's own markup is left alone, so `<p>From {{data.Name}}</p>` still
renders a paragraph. A value in `Subject` or `To` has its line breaks replaced by a space and is
otherwise unchanged, since a header is not HTML.

There is no syntax for inserting a value as raw HTML. An entry's fields can hold whatever a public
form submitted, and a template cannot tell that field from one an editor wrote.

This includes rich text and markdown fields. A body of `{{data.Body}}`, where that field holds
`<p>Hello <b>world</b></p>`, arrives showing those tags as text rather than a bold "world". Versions up to
4.4.1 rendered it as HTML. Write the markup in the template itself and put only plain values in it.

`To` must resolve to exactly one email address. A value with a comma or semicolon, a line break, or
anything that does not parse as an address fails the action with "The 'To' parameter must resolve to
exactly one email address." and nothing is sent, whichever provider is configured.

The same applies to an `Email` inside a `Conditional`: its parameters are resolved when the child
runs, not as part of the branch's JSON.

## Attachments in a workflow email

An `Email` action takes an optional `Attachments` parameter naming files stored by the
BarakoCMS.Files module:

```json
{
  "Type": "Email",
  "Parameters": {
    "To": "registrar@example.com",
    "Subject": "Programme for {{data.Name}}",
    "Body": "<p>The programme {{data.Name}} chose is attached.</p>",
    "Attachments": "{{data.Programme}}"
  }
}
```

`Attachments` is a placeholder for a field of the entry, or a list of ids and placeholders separated
by commas, semicolons or line breaks. A file is named by its id, or by a link to `/api/files/{id}`
or `/api/public/files/{id}`. A link with `?w=` attaches the original file, not the resize. When the
whole parameter is one placeholder and the field holds a list, every item of the list is attached.

**Which files.** Public files only. A file is attached when all of these hold:

- the entry the workflow is running for names the file in one of its fields, by id or by link;
- the file is stored in that entry's tenant;
- the file is public, which means anyone holding its URL can already download it from
  `/api/public/files/{id}`.

Nobody's rights are consulted. A workflow runs with no signed-in caller, and nothing on an entry
records who chose the files its fields name: the last person to save it, approve it, restore it or
import it need not be the person who put the file id there. So the action sends only what is
already public, and it does not matter who can write the field or who `To` resolves to: the
recipient receives nothing the file's URL would not give them.

The cost is that a private file cannot be emailed by a workflow yet. A receipt, a contract or
anything else uploaded without the public flag is refused, whoever uploaded it and whoever saved
the entry. That waits for a file field that ties a file to its entry when it is uploaded (#668).
Until then, mark the file public at upload if it may go out by email, or send a link the recipient
signs in to open.

A deleted entry has no fields left and attaches nothing.

**Limits.** From configuration:

| Key | Default | Meaning |
|---|---|---|
| `Workflows:Email:Attachments:MaxCount` | 5 | Files on one email |
| `Workflows:Email:Attachments:MaxFileBytes` | 10485760 (10 MB) | Size of one file |
| `Workflows:Email:Attachments:MaxTotalBytes` | 15728640 (15 MB) | Size of all files on one email |

Zero in any of them turns attachments off. A value that is not a whole number of zero or more fails
every email that names an attachment, with the key in the reason, and leaves other emails alone.

A file whose record is over the per-file limit is refused before its bytes are read. Past that
check the Files module reads a whole file into memory, so the limits bound what is sent and what
the action keeps, not what one read can load for a record whose size is wrong. The files of one
email stay in memory while it is sent. At the default limits that is about 15 MB of files, and the
provider adds its own copies: SMTP encodes while it writes to the relay, so about 20 MB an email;
Resend takes the files as base64 text inside one JSON request, which comes to roughly 100 MB an
email at peak. These are estimates from the copies each path makes, not measurements. A node can be
sending as many emails at once as `Workflows:RunnerConcurrency` allows, so multiply by that.

Your provider has its own ceiling on message size, and encoding adds about a third. Neither
provider module reports a size refusal differently from any other failure, so an email the provider
refuses for its size is retried like one: five attempts in all, each reading and uploading the
files again, before the action is left failed. Keep `MaxTotalBytes` under what your provider takes.

**When it cannot attach.** The action fails, the reason is on the run, and nothing is sent. An email
never goes out with a file missing. A file that is not named by the entry, is not in the tenant, is
not public or does not exist gives one reason, "Attachment N cannot be attached", that does not say
which of those it was. The others say what was wrong: an empty field or list, a value that is not a file id
or link, a limit passed, no BarakoCMS.Files module, and an email provider that does not send
attachments. None of these is retried, because a retry would get the same answer. A file store that
fails while it is read is retried.

The attachment carries the name the file was uploaded with, without any path, control characters
or text direction marks, and the type the upload was checked against (`application/octet-stream`
for a file stored before uploads were checked).

Both shipped providers send attachments. A provider of your own implements the two `IEmailService`
members that take attachments; until it does, an email that names one fails and says so.

A workflow stored before this release that already had an `Attachments` parameter on an Email
action was sending without it. After the upgrade that action attaches the files or fails with a
reason on the run.

## Auditing

Changing email settings is recorded as `settings.email.changed` in the audit trail, with which
fields changed and never their values. It is a SuperAdmin action rather than an Admin one, because
redirecting where the system's mail comes from redirects every password reset and every verification
token in the deployment.
