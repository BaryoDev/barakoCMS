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

With Turnstile enabled and no secret set, every submission is refused and an error is logged.

## Notification

Not this module's job. Add a workflow on `Created` for the form's content type with an Email
action. The submission is written through `IContentWriter` like any other entry, so the workflow
fires for it.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
