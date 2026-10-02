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
400. Left out or null, the form keeps what it has; an empty string turns verification off. Turning
the form itself off and on again keeps the setting too, so a console that does not know the field
cannot drop verification by toggling the form. If the field has stopped being an email field in the
meantime, turning the form on is refused with 400 until the request says what to verify.
`GET /api/forms` and the form definition both report `verifyEmailField`, and the definition marks
that field `required` whatever the type says.

The visitor asks for a code, then sends it with the submission:

```
POST /api/public/forms/race-signup/email-code
{ "email": "ana@example.com", "honeypot": "", "turnstileToken": null }
```

- **202** `{ "accepted": true }`. The same answer whether the mail went out, the provider refused
  it or the provider did not answer within `SendTimeoutSeconds`, and for a filled honeypot, which
  sends nothing. A failed send is logged with the tenant, the form and the kind of failure, never
  the address.
- **400** the address is not one bare mailbox (see below), or the Turnstile check failed.
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

Both routes take the address only as one bare mailbox: ASCII letters, digits and
``.!#$%&'*+/=?^_`{|}~-`` before the `@` (at most 64 characters, no dot first, last or doubled), a
dotted host name of letters, digits and hyphens after it, and at most 254 characters in all. A
display name (`Ana <ana@example.com>`), a comment, quotes, a trailing dot, an address literal and
anything outside ASCII are refused with 400, on the code route as `email` and on submit as
`data.<field>`. The email field type itself is looser, and a mail library reads several of those
spellings as the same mailbox, so without this one mailbox could be sent a fresh five codes under
each spelling. An alias the mail provider resolves, such as a plus tag, is still a different address
here.

What a code is:

- six digits from a cryptographic random source, stored only as a BCrypt hash
- good for 10 minutes and for one accepted submission
- dead after 5 checks, right or wrong, counted before the comparison and under a lock per address
- bound to the tenant, the form and the address it was sent to. The address is trimmed and
  lowercased the same way when the code is sent and when it is checked.
- replaced by the next code sent to the same address, on any form of the tenant

A refusal with no live code runs the same hash comparison as a wrong guess, but a wrong guess also
writes the attempt, so the two can differ by a few milliseconds. Someone who measures that learns
whether a code is outstanding for an address, and each such probe uses one of that code's five
attempts.

An accepted submission adds an audit event, `form.email.verified`, whose target is the entry. It
holds the form and the field name, not the address and not the code. These come from anonymous
traffic, one per accepted submission, and the audit chain can fork when two are written at the same
moment, as it can for any two audited actions.

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
caller that five codes were asked for at that address in the last hour. No other answer on this
route depends on the address.

The limits can be turned on a known address. Five requests in an hour stop that address getting
another code until the window moves, and five wrong submissions kill its live code. Either costs
the caller their own per client budget, and Turnstile when it is on.

The per client limit counts the address the connection comes from. Behind a proxy with forwarded
headers not configured, every visitor shares one budget, as they already do for submissions.

The mail is built from the form's display name and the code. Nothing from the request goes into it.

Two tables hold this: `form_email_verifications`, one row per address, and `form_email_budgets`, one
row per form, which also remembers the verified field while the form is off. The address row's id
is a SHA-256 of the address with no key and no salt. That keeps addresses out of a plain read of the
table. It does not stop someone with database access testing whether a given address has a row,
which includes an address that asked for a code and never submitted. Neither table has an index:
rows are loaded by id, and the cleanup reads one tenant's rows by time on every send, a scan the
limits above keep small. Rows whose code and window have both passed are removed, up to 200 at a
time, whenever a code is sent in the same tenant. Nothing removes them in a tenant where no code is
asked for again.

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

Every field also carries `currency` and `scale`. Both are null except on a `money` field that
declares a currency, where `currency` is its ISO 4217 code and `scale` is the most decimal places an
amount may carry: the field's own if it declares one, the currency's otherwise. Such a field takes
the amount as a JSON number or as the plain decimal text an input holds (`"12.50"`), and stores a
number either way. An amount with more decimal places than `scale`, or text with a thousands
separator, a currency symbol or an exponent, is a 400. See `docs/money-fields.md` in the core
repository.

## Configuration

Section `Modules:Forms`:

| Key | Default | Meaning |
| --- | --- | --- |
| `PermitLimit` | `5` | submissions per client IP per window, across every form |
| `WindowSeconds` | `600` | the window |
| `PerForm:{slug}:PermitLimit` | the shared `PermitLimit` | submissions per client IP per window to that one form |
| `PerForm:{slug}:WindowSeconds` | the shared `WindowSeconds` | the window for that form |
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
| `EmailVerification:SendTimeoutSeconds` | `10` | how long a code request waits for the email provider |

With Turnstile enabled and no secret set, every submission is refused and an error is logged. An
`EmailVerification` value below 1 stops the host at startup with an error naming the setting: a
limit cannot be turned off by setting it to zero. The send deadline works by cancelling the
provider, so it holds for a provider that honours cancellation, which the Resend and SMTP modules
do.

### A limit for one form

No form has its own limit unless `PerForm` names it. A busy form can be given one:

```yaml
- Modules__Forms__PerForm__registration__PermitLimit=20
- Modules__Forms__PerForm__registration__WindowSeconds=60
```

Submissions to `registration` are then counted per client IP against those numbers, and no longer
against the shared limit. Every other form stays on the shared one. The limit applies to the submit
route only; the definition route is under the API's global limit, as before. The email code route
has a policy of its own, `forms-email-code`, per client IP across every form with the
`EmailVerification:RequestsPerClient` numbers, and `PerForm` does not change it: what bounds one
form's sending is `EmailVerification:CodesPerForm`.

A value left out takes the shared one as configured. A value that is not a whole number above zero
stops the host at startup with the setting named. A slug with a dash cannot be exported as an
environment variable from a POSIX shell; set it in `appsettings.json` or in the compose file.

The slug is matched in any case, and a request for another spelling of it is counted in the same
bucket. The tenant is not part of the bucket, because a caller chooses `X-Tenant` and the host: two
tenants with a form of the same slug share the numbers, and one client IP has one count across both.
`FormRateLimitTests` covers this.

### A limit for another route of this module

A new route gets its own policy the way submit does: register it in `FormsModule.ConfigureServices`
with `services.Configure<RateLimiterOptions>(o => o.AddPolicy("forms-<purpose>", ...))`, read its
numbers from `FormsOptions`, and name it on the endpoint with `RequireRateLimiting`. Key it on
something the caller cannot vary per request, such as the client IP or a configured slug. The
limiter runs before the body is read, so a value from the body (an email address, say) cannot be a
key there; count that inside the endpoint. Do not take a name the core reserves (`auth`,
`telemetry`, `registration`, `site-share`, `logout`, `delivery`).

## Notification

Not this module's job. Add a workflow on `Created` for the form's content type with an Email
action. The submission is written through `IContentWriter` like any other entry, so the workflow
fires for it.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
