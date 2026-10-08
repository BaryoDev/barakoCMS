# Email templates

An Email workflow action can carry its own `Subject` and `Body`, or it can name a template stored as
content. A template is reworded once, in the console like any entry, and every workflow that names
it sends the new text from then on.

## Setting it up

Apply the `email` blueprint in the tenant:

```http
POST /api/content-types/blueprints/email
```

It creates two types, neither publicly deliverable:

| Type | Fields |
| :--- | :--- |
| `email-template` | `Name`, `Slug`, `Subject`, `Body` (markdown), `Layout` (a reference to an `email-layout`), `Locale` |
| `email-layout` | `Name`, `Header` and `Footer` (markdown), `Logo` (url), `LogoAlt`, `Background`, `Text` and `Accent` (hex colours such as `#17458F`) |

`Subject`, `Body`, `Header` and `Footer` each hold at most 262,144 characters, the same cap a
workflow parameter has. `Locale` is stored for the editor's benefit; nothing picks a template by it.

Templates and layouts are tenant content. A workflow only ever finds the templates of its own
tenant, by id or by slug.

## Naming a template in a workflow

```json
{
  "type": "Email",
  "parameters": { "To": "{{createdBy.email}}", "Template": "welcome" }
}
```

`Template` is the template's id or its slug. With it, `Subject` and `Body` are not needed, and
writing either beside it is refused with 400 on `actions[i].parameters.Template`. A workflow
without `Template` sends its own `Subject` and `Body` exactly as before.

`Template` is never resolved as a placeholder: which template goes out is the workflow's choice,
not a value in the entry. `To` and `Attachments` work as they always have.

Saving the workflow (and `POST /api/workflows/validate`) reads the template and adds warnings on
`actions[i].parameters.Template`: the same placeholder warnings an inline body gets, each prefixed
with the field they are in, such as `In the template's Body:`, and a warning when the template does
not exist or is not published. These are warnings, not refusals, because the template can be
written or published after the workflow is saved. Only the workflow's own actions are read, not the
children of a Conditional, and at most 20 distinct templates per save.

## When a template is sent

Only a **published** template is sent. When the action runs it fails permanently, so the run is
not retried, with a message saying why, when:

- no template of that id or slug exists in the tenant, including one that was erased;
- the template is a draft, archived or scheduled (`Email template 'welcome' is Draft. Only a published template is sent.`);
- it names a layout that is missing or not published;
- its subject or body is empty, or a text is past the 262,144 character cap.

A slug held by more than one template (uniqueness is checked on write, not enforced) names the
oldest published one.

## Rendering

1. The body, and the layout's header and footer, are markdown. Raw HTML in them is turned off: a
   tag is shown as text. The only markup is what markdown makes and the fixed layout around it.
2. Every placeholder is kept exactly as written while the markdown renders, so
   `[Open]({{links.site "/bookings"}})` and `_{{data.first_name}}_` work.
3. The result is resolved by the same engine and with the same encoding as an inline body: every
   value is HTML encoded, the subject loses its line breaks. References, loops, links and formats
   all work (see [approval-by-configuration.md](approval-by-configuration.md#placeholders)), and what
   a template names beyond the action's own parameters (an author, a reference) is read too.
4. A placeholder the engine leaves as written cannot add markup: its quotes and angle brackets are
   encoded. A link keeps its address only when it is `http`, `https` or `mailto`, and an image only
   when it is `http` or `https`; anything else is dropped and the link shows as text.

A loop's markers are placeholders too, so keep a loop inside one paragraph or list item:

```markdown
Runners: {{#each data.Runners}}{{data.Name}}, {{/each}}
```

A loop spread over several blocks repeats half of a block's tags.

### The layout

A template that names a layout is wrapped in it: logo, header, body, footer, with the layout's
colours (an invalid colour falls back to the default). A template with no layout gets the same
shell filled from the tenant's published `site` entry ([site-settings.md](site-settings.md)): its
`Name` as the header, `Logo`, `LogoAlt`, `Copyright` as the footer, and `pageBg`, `ink` and `accent`
from `Colors`.

The email is sent as HTML. There is no plain text part yet: `IEmailService` takes one body.

## Preview

```http
POST /api/email-templates/{id}/preview
{ "entryId": "3fa85f64-5717-4562-b3fc-2c963f66afa6" }
```

`{id}` is the template's id or slug. It renders the subject and body against the entry and sends
nothing. It answers:

```json
{
  "subject": "Welcome, Ana",
  "html": "<!DOCTYPE html>...",
  "status": "Draft",
  "sendable": false,
  "problem": "The template is Draft. Only a published template is sent.",
  "warnings": [{ "field": "Body", "message": "'{{nope}}' names 'nope', which is not a placeholder, so it is sent as written." }],
  "notes": []
}
```

- It needs `manage_workflows`, and the caller must be able to read the template and the entry,
  checked as `GET /api/contents/{id}` checks them. A template the caller cannot read answers 404,
  like one that does not exist. An entry they cannot read answers 403.
- The entry renders as the caller reads it. A Sensitive or Hidden field they may not see renders
  empty, and a warning on `entryId` says how many fields were withheld. A reference is followed only
  to entries the caller may read. The author placeholders show sample values. A send resolves
  against the whole entry, so a preview can show less than the email, never more.
- A draft renders, so it can be checked before it is published; `sendable` says whether a workflow
  would send it now. A missing layout or an empty body comes back as `problem` with no `html`.
- It has its own rate limit, `email-preview`: 30 requests a minute per IP.

There is no test send. A preview never sends.
