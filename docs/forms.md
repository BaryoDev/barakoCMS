# Forms

`BarakoCMS.Forms` is where a website's contact form posts to. It is a module, not core: reference
the package and `AddBarakoCMS` discovers it. The module README
([BarakoCMS.Forms/README.md](../BarakoCMS.Forms/README.md)) is the short version; this page is the
shape and the reasoning.

## The shape

A **form definition** belongs to a tenant and holds a name, a display name, fields, notify
addresses and an enabled flag. The name is a lower-case slug, unique within the tenant, and it is
the public URL segment, so it cannot be renamed once a site points at it. Each field has a name,
a type from core's `FieldTypeRegistry` (`string`, `text`, `email`, `int`, `bool`, `date`, and the
rest a content type accepts) and a required flag. At most 50 fields and 20 notify addresses.

A **submission** is one visitor's field values plus when they arrived. It is its own Marten
document (`form_submissions`), tenant scoped, with `Sensitivity = Sensitive` set on creation. It
is deliberately not a `Content` entry: content is what the public delivery API serves, and a
submission stored as content would be one flag away from an anonymous listing.

Admin endpoints live under `/api/forms`. `manage_forms` covers the definition CRUD;
`view_form_submissions` covers the submission list, the single read and the CSV export. The Admin
role is granted both by the module's seeder; a role created at runtime is granted either through
`POST /api/roles` like any other capability.

## The anonymous endpoint

`POST /api/public/forms/{name}` with a JSON object of field values. `202 Accepted` with
`{ "id": "..." }`. It is anonymous by necessity and a target by consequence, so the protections are
the main body of the module, applied in this order:

1. **Rate limit, per resolved client address.** Its own policy, `forms`, at
   `Modules:Forms:SubmissionsPerMinute` (default 5) per minute, the same pattern as the
   Diagnostics `telemetry` policy and tighter than the global 100. The sixth in a minute is `429`;
   a different address is unaffected. Behind a proxy, configure forwarded headers so the address
   the limiter sees is the visitor's and not the proxy's.
2. **Body cap.** `Modules:Forms:MaxBodyBytes` (default 32768). The body is read up to the cap and
   refused with `413` past it, before any JSON is parsed, so the cap holds whether or not the
   server in front enforces one.
3. **Honeypot.** Any non-empty value in the field named by `Modules:Forms:HoneypotField` (default
   `website`) is answered `202` with a random id and nothing is stored, looked up or emailed.
   Render that field hidden with CSS; a person never fills it. A definition may not declare a
   field by that name, or real visitors could never submit.
4. **The form must exist and be enabled**, else `404`. A disabled form is indistinguishable from
   a missing one.
5. **Validation against the definition.** An unknown field, a missing required field, a value that
   fails its type check, or a value whose JSON text is longer than `Modules:Forms:MaxFieldChars`
   (default 4000) is `400` naming the field. Values are trimmed; empty strings count as absent.
6. **Store**, then **notify**: one email per notify address through the registered
   `IEmailService`, awaited for at most `Modules:Forms:NotifyTimeoutSeconds` (default 10). A
   failed or timed-out send is logged with the form name, the tenant and the submission id, and
   the `202` goes out anyway. There is no retry. A queue with retries is #106.

Nothing a visitor typed reaches a log line, and the response carries the id and nothing else.

## Never through public delivery

`GET /api/public/{type}`, `/api/public/{type}/{slug}` and `/api/public/{type}/search` query
`Content` for a publicly deliverable content type. Submissions are not content, so those routes
answer `404` for a form's name and for a submission's id. `FormSubmissionTests` holds this with a
control: a public content type sharing the form's name is served, and the submission still is not.

## Settings

All under `Modules:Forms`.

| Key | Default | Effect |
|---|---|---|
| `HoneypotField` | `website` | The field a bot fills and a person never sees |
| `SubmissionsPerMinute` | `5` | Per client address, across every form; read at startup |
| `MaxBodyBytes` | `32768` | Bodies past this are `413` |
| `MaxFieldChars` | `4000` | A value's JSON text past this is `400` |
| `ExportMaxRows` | `10000` | The most rows one CSV export returns, newest first |
| `NotifyTimeoutSeconds` | `10` | How long a notification send may hold the `202` |

## Submissions are personal data

A contact form collects a name and a way to reach the person, so every submission is personal
data from the moment it exists, which is why the document is Sensitive by default and why it has no
public read path. The list, the single read and the CSV all sit behind `view_form_submissions`;
grant it to the people who answer the mailbox and nobody else. The CSV export leaves access
control behind the moment it is downloaded, the same as a Portability bundle. Deleting a form
deletes its submissions, so a mailbox nobody reads any more should be deleted rather than left.
Retention and erasure per submission are not built; #301 is where erasure is tracked.
