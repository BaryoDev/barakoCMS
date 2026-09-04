<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Forms logo" />
  <h1>BarakoCMS.Forms</h1>
  <p><em>Somewhere for a contact form to go.</em></p>
</div>

---

Define a form and its fields, point a website's form at one anonymous endpoint, and read what
arrives in the admin, as a list, one at a time, or as a CSV. Each submission emails the addresses
the form names.

## Enable it

Reference the package and restart. `AddBarakoCMS` discovers it.

```sh
dotnet add package BarakoCMS.Forms
```

The package reference plus a restart is the install: `AddBarakoCMS` discovers the module from the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Forms`). Unset, every referenced module runs and the API logs one
warning saying so. To name it by hand instead, put `modules.Add(new BarakoCMS.Forms.FormsModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.

## Endpoints

| Method & path | Purpose | Access |
|---|---|---|
| `POST /api/forms` | Define a form | `manage_forms` |
| `GET  /api/forms` | List the tenant's forms | `manage_forms` |
| `GET  /api/forms/{name}` | Read one | `manage_forms` |
| `PUT  /api/forms/{name}` | Replace its fields, notify addresses and enabled flag | `manage_forms` |
| `DELETE /api/forms/{name}` | Remove it and every submission it received | `manage_forms` |
| `POST /api/public/forms/{name}` | Submit to it | Anonymous |
| `GET  /api/forms/{name}/submissions` | List submissions, paged, `?from=&to=` | `view_form_submissions` |
| `GET  /api/forms/{name}/submissions/{id}` | Read one | `view_form_submissions` |
| `GET  /api/forms/{name}/submissions.csv` | Export, newest first, capped | `view_form_submissions` |

Admin holds both capabilities from the module's seeder. Designing a form and reading what people
sent are two grants, because the person who answers the mailbox is not usually the person who
designs the form.

## A form

```json
{
  "name": "contact",
  "displayName": "Contact us",
  "fields": [
    { "name": "name", "type": "string", "required": true },
    { "name": "email", "type": "email", "required": true },
    { "name": "message", "type": "text", "required": true }
  ],
  "notifyAddresses": ["hello@example.com"],
  "enabled": true
}
```

Field types are core's `FieldTypeRegistry`, the same set a content type uses, and the value a
visitor sends is checked against the type. A form name is unique within a tenant and cannot be
renamed, because it is the URL a website already points at.

## The public endpoint is a target

`POST /api/public/forms/{name}` takes a JSON object of field values and answers `202` with the
submission id. Because anyone can call it:

- **Rate limit per client address**, `Modules:Forms:SubmissionsPerMinute` (5), tighter than the
  global budget. The sixth in a minute is `429`.
- **Honeypot.** Any value in the field named by `Modules:Forms:HoneypotField` (`website`) is
  answered `202` and stored nowhere. Render that field hidden; a person never fills it, a bot does.
  A form may not declare a field by that name.
- **Body cap**, `Modules:Forms:MaxBodyBytes` (32768). Larger is `413`, checked before anything is
  parsed.
- **Field cap**, `Modules:Forms:MaxFieldChars` (4000) per value, and unknown fields, missing
  required fields and wrong types are `400`.
- **Submissions are their own document**, marked Sensitive, never content. The public delivery
  routes (`/api/public/{type}`, `/{slug}`, `/search`) cannot serve them, and a test holds that
  even when a public content type shares the form's name.

Nothing a visitor typed reaches a log line.

## Notifications

Each submission is emailed to the form's `notifyAddresses` through the registered `IEmailService`
(`BarakoCMS.Email.Smtp` or `BarakoCMS.Email.Resend`; the mock provider logs and delivers nothing).
The send is best effort: it is awaited for at most `Modules:Forms:NotifyTimeoutSeconds` (10), a
failure is logged and the `202` goes out regardless, with the submission stored. There is no
retry yet. A queue with retries lands with #106.

## Submissions are personal data

A submission is whatever a visitor typed, which is a name and a way to reach them at minimum.
Treat the list and the CSV accordingly, and delete the form when the mailbox is no longer read:
the delete removes its submissions too.

## Settings

All under `Modules:Forms` (`Modules__Forms__SubmissionsPerMinute` as an environment variable).

| Key | Default | What it does |
|---|---|---|
| `HoneypotField` | `website` | The field a bot fills and a person never sees |
| `SubmissionsPerMinute` | `5` | Per client address, across every form |
| `MaxBodyBytes` | `32768` | Request bodies past this are `413` |
| `MaxFieldChars` | `4000` | One value's JSON text past this is `400` |
| `ExportMaxRows` | `10000` | The most rows one CSV returns |
| `NotifyTimeoutSeconds` | `10` | How long a notification send may hold the `202` |

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome, including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
