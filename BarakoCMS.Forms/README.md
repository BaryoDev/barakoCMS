# BarakoCMS.Forms

Lets a public visitor submit a form. The form is a content type: its fields, types and required
flags are the form, and a submission is an ordinary entry of that type. A widget reads the form
definition and draws the inputs, and a workflow on Created for the type sends the notification.

## Install

```sh
dotnet add package BarakoCMS.Forms
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
modules run (`BarakoCMS__Modules__Enabled=Forms`). To name it by hand, put
`modules.Add(new BarakoCMS.Forms.FormsModule())` in the `AddBarakoCMS` callback. See `MODULES.md`
in the repository.

## Making a content type a form

Create the type as usual, then mark it:

```
PUT /api/forms/contact-request
Authorization: Bearer <token with manage_forms>

{ "enabled": true }
```

`{ "enabled": false }` turns it off again. `GET /api/forms` lists the types that accept
submissions. Admin is granted `manage_forms` when the module seeds.

A visitor can fill in a field only when it is Public and its type is one of `string`, `text`,
`int`, `decimal`, `money`, `bool`, `date`, `datetime`, `time`, `email`, `url` or `choice` (and their
aliases). A choice value has to be one of the field's options, and a multiple choice field takes a
list.
A field named `slug` is never submittable. Enabling a type is refused with 400 when a required field
is not submittable, or when the type is a singleton.

## Endpoints

| Method | Route | Who | Purpose |
| --- | --- | --- | --- |
| GET | `/api/public/forms/{slug}` | anyone | the fields a widget draws |
| POST | `/api/public/forms/{slug}` | anyone, rate limited | submit |
| POST | `/api/public/forms/{slug}/email-code` | anyone, rate limited | email a one-time code, for a form that verifies an email field |
| PUT | `/api/forms/{contentType}` | `manage_forms` | mark a type as a form, or unmark it |
| GET | `/api/forms` | `manage_forms` | list forms, paged |

The slug is the content type name. A type that is not marked as a form answers 404 on both public
routes.

### Submit

```json
{
  "data": { "name": "Ana", "email": "ana@example.com", "message": "Hello" },
  "honeypot": "",
  "turnstileToken": null
}
```

- **202** `{ "accepted": true }`. The entry is stored as Draft, document sensitivity Sensitive, with
  no owner, so the delivery API never serves it. Nothing in the request can change those three.
- **400** ProblemDetails. A field failure is named `data.<field>`; an unknown or non-submittable
  field is refused with the same message either way. A failed Turnstile check is named
  `turnstileToken`.
- **404** the slug is not a form.
- **429** too many submissions from this client IP.

A non-empty `honeypot` gets the same 202 and nothing is stored.

## Verifying an email field

A form can refuse a submission until the visitor has proved they read mail at the address in one of
its email fields. It is off unless the form turns it on, and a form that has not behaves as above.

```
PUT /api/forms/race-signup
Authorization: Bearer <token with manage_forms>

{ "enabled": true, "verifyEmailField": "email" }
```

`verifyEmailField` has to name an email field a visitor can fill in, or the request is refused with
400. Left out, the form keeps what it has; an empty string turns verification off. Turning the form
itself off forgets the setting. `GET /api/forms` and the form definition both report
`verifyEmailField`, and the definition marks that field `required` whatever the type says.

The visitor asks for a code, then sends it with the submission:

```
POST /api/public/forms/race-signup/email-code
{ "email": "ana@example.com", "honeypot": "", "turnstileToken": null }
```

- **202** `{ "accepted": true }`. The same answer whether the mail went out or the provider refused
  it (that is logged), and for a filled honeypot, which sends nothing.
- **400** the address is not one email address, or the Turnstile check failed.
- **404** the slug is not a form, or the form does not verify an email field.
- **429** past a limit, see below. Nothing is stored or sent.

```json
{
  "data": { "name": "Ana", "email": "ana@example.com" },
  "emailVerificationCode": "482913"
}
```

The submission is accepted only with the live code for the address in the verified field. Any other
case is a 400 named `emailVerificationCode` with one message, whether the code is wrong, expired,
already used, guessed at too often, sent to another address, sent for another form or never sent. A
missing address is a 400 named `data.<field>`. The code is checked last, after every other check
has passed, so a submission that fails for another reason does not use up an attempt.

What a code is:

- six digits from a cryptographic random source, stored only as a BCrypt hash
- good for 10 minutes and for one accepted submission
- dead after 5 checks, right or wrong, counted before the comparison and under a lock per address
- bound to the tenant, the form and the address it was sent to. The address is trimmed and
  lowercased the same way when the code is sent and when it is checked.
- replaced by the next code sent to the same address, on any form of the tenant

An accepted submission adds an audit event, `form.email.verified`, whose target is the entry. It
holds the form and the field name, not the address and not the code.

Limits on sending, each answered with 429:

| Limit | Default | Counted |
| --- | --- | --- |
| per client IP | 5 per 600 seconds | in memory, across every form |
| per address | 5 per 60 minutes | stored, per tenant, across every form |
| per form | 100 per 60 minutes | stored, per tenant, across every address |

So one form sends at most 100 messages an hour whatever addresses and IPs a caller cycles through,
and one address gets at most 5 an hour from a tenant. Past the per form limit nobody can get a code
for that form until the window moves, real visitors included, so raise it for a busy sign-up and
turn Turnstile on, which this route checks too when it is enabled. The per address 429 tells a
caller that five codes were asked for at that address in the last hour. Nothing else in an answer
depends on the address.

The mail is built from the form's display name and the code. Nothing from the request goes into it.

Two tables hold this: `form_email_verifications`, one row per address with the hash of the address
as its id, and `form_email_budgets`, one row per form. Rows whose code and window have both passed
are removed, up to 200 at a time, whenever a code is sent in the same tenant. Nothing removes them in
a tenant where no code is asked for again.

### Definition

```json
{
  "slug": "contact-request",
  "displayName": "Contact request",
  "description": "",
  "fields": [
    { "name": "email", "displayName": "Email", "type": "email", "required": true, "validationRules": {}, "options": [], "multiple": false },
    { "name": "topic", "displayName": "Topic", "type": "choice", "required": true, "validationRules": {},
      "options": [ { "value": "sales", "label": "Sales" }, { "value": "support", "label": "Support" } ],
      "multiple": false }
  ]
}
```

`options` lists a choice field's options in display order and is empty for any other type.
`multiple` says whether the field takes a list.

## Configuration

Section `Modules:Forms`:

| Key | Default | Meaning |
| --- | --- | --- |
| `PermitLimit` | `5` | submissions per client IP per window, across every form |
| `WindowSeconds` | `600` | the window |
| `MaxFieldLength` | `10000` | the longest string a field may hold |
| `Turnstile:Enabled` | `false` | require a Cloudflare Turnstile token |
| `Turnstile:SecretKey` | none | the Turnstile secret; set it through the environment |
| `EmailVerification:CodeLifetimeMinutes` | `10` | how long a code works |
| `EmailVerification:MaxAttempts` | `5` | checks one code survives |
| `EmailVerification:RequestsPerClient` | `5` | code requests per client IP per window, across every form |
| `EmailVerification:RequestWindowSeconds` | `600` | that window |
| `EmailVerification:CodesPerAddress` | `5` | codes sent to one address per window, per tenant |
| `EmailVerification:CodesPerForm` | `100` | codes one form sends per window |
| `EmailVerification:WindowMinutes` | `60` | the window for the two limits above |

With Turnstile enabled and no secret set, every submission is refused and an error is logged. An
`EmailVerification` value below 1 is read as 1.

## Notification

Not this module's job. Add a workflow on `Created` for the form's content type with an Email
action. The submission is written through `IContentWriter` like any other entry, so the workflow
fires for it.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
