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
children of a Conditional, and at most 20 distinct templates per save; past that one warning on
`actions` says the rest were not checked.

## When a template is sent

Only a **published** template is sent. When the action runs it fails permanently, so the run is
not retried, with a message saying why, when:

- no template of that id or slug exists in the tenant, including one that was erased;
- the template is a draft, archived or scheduled (`Email template 'welcome' is Draft. Only a published template is sent.`);
- it names a layout that is missing or not published;
- its subject or body is empty, or a text is past the 262,144 character cap;
- the body as HTML in its layout is past that cap. The resolver leaves a text past the cap as
  written, so this is refused rather than sent with no placeholder filled. The save and the preview
  report it the same way.

A slug held by more than one template (uniqueness is checked on write, not enforced) names the
oldest published one.

## Rendering

1. The body, and the layout's header and footer, are markdown. Raw HTML in them is turned off: a
   tag is shown as text. The only markup is what markdown makes and the fixed layout around it.
2. Every placeholder is kept exactly as written while the markdown renders, so
   `[Open]({{links.site "/bookings"}})` and `_{{data.first_name}}_` work.
3. The result is resolved by the same engine as an inline body. Every value in the body is HTML
   encoded, braces included (`&#123;` and `&#125;`), so two values cannot pair into something that
   reads as a placeholder. The subject is encoded as an inline subject is: it loses its line breaks. References, loops, links and formats
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

## Expressions, datasets and row loops

A template works out values, and it can list rows from a saved query, with no code. This is an
invoice:

```text
Invoice {{data.Number}} for {{data.Customer.Name}}
{{#each lines}}{{row.Product.Name}}  {{row.Qty}} x {{row.Price | money}} = {{row.Total | money}}
{{/each}}
Subtotal {{= sum(lines.Total) | money}}
Tax {{= round(sum(lines.Total) * data.TaxRate, 2) | money}}
Total {{= round(sum(lines.Total) * (1 + data.TaxRate), 2) | money}}
```

The template's `Datasets` field names the saved query behind `lines`:

```json
{ "lines": "invoice-lines" }
```

and the saved query ([connectors.md](connectors.md#saved-queries-as-text)) is:

```text
from invoiceLine where Invoice = @id and status != Archived order by createdAt select Product.Name, Qty, Price, Total limit 200
```

`@id` is the entry the email is sent for, so the rows are that invoice's lines.

**`{{= expression }}`** is an [expression](expressions.md), with `data.Field`, `createdBy.name`,
`transition.name` and the other names a plain hole reads. `| money`, `| date`, `| upper` and
`| lower` follow it as they follow a name. A hole that cannot be read, or an expression that has no
value, is left as written and listed as a warning on save and in the preview.

**A dataset** is a name from `Datasets`. Up to 10 names, each a saved query, each read once per send.
Inside an expression, `lines.Total` is the column `Total` of every row, and these work on it:
`sum`, `count`, `avg`, `min`, `max`, `first`, `last`. `lookup(dataset, keyField, key, resultField)`
reads one value from another dataset's row, one level only.

**`{{#each lines}}...{{/each}}`** repeats its text for each row, and inside it `{{row.Qty}}` and
`{{row.Product.Name}}` read that row. A loop over a dataset is the same loop as over a reference,
with `row` where a reference loop has `data`.

What is read and what is refused:

- Rows are read as the user the run acts as, with the same read rights as `GET /api/contents`,
  in the template's tenant only. A field that user may not read fails the send, naming the field.
  Saving a template, or a workflow that names one, is refused for a saver who could not read every
  field the dataset selects.
- A template loop renders at most 200 rows. A dataset with more fails the send with a message
  that names the dataset, since an invoice cut short is wrong. A reference loop stops at 50 and
  notes it, as before.
- All of a template's datasets are read within 30 seconds together, one after another, and the
  first one that cannot be read ends the reading. A read that timed out or failed is retried by the
  workflow. A refusal, a missing query and a dataset over 200 rows are not.
- A loop over a name the template does not declare in `Datasets` is refused when the template is
  sent, naming the dataset, and is a warning in the preview and when a workflow that names the
  template is saved.
- One render evaluates at most 100,000 placeholders, counting every pass of every loop.
- The result is at most 1,048,576 characters, and a template at most 262,144, whether read for
  holes or rendered. Reading a template that takes more than 2 seconds is refused. Over either, the
  send fails with a message, and nothing is truncated.
- Loops do not nest. A loop inside a loop is sent as written, and a warning names it.
- Values are never read as template text. A value holding `{{...}}` or markup is encoded for where it
  lands: HTML in a body, JSON string escaping in a JSON body, URL encoding in a path segment, and a
  line break turned into a space in a subject. There is no second pass over the result.
- `today()` and `now()` use the site's time zone, and UTC when it has none.
- A preview reads datasets as the caller. A dry run reads none and shows the holes as written.
- A dataset is read for the send and for the preview only. No export reads one, and no log line
  holds a value from a row.
- Markdown renders around the holes: they are put aside before markdown reads the text and
  restored after, so `_`, `|` and `*` in a value cannot change the markup. A table row may hold
  holes, and a loop may wrap table rows.

A **Request** action does not take expressions, loops or row names yet. A Request template that
holds one is refused before anything is sent, as is one reading a Hidden, Sensitive or Members
field, in any form. An Email and an SMS may send any field the workflow's author may read.

A `createdBy.email` or `transition.by.email` built inside an expression is read like the plain form:
an `UpdateField` or `CreateTask` parameter holding one gets the same warning on save.

A stored template that holds a literal `{{=` was sent as written before and evaluates now.
`db-migrate --status` lists the stored templates, layouts and workflow parameters that hold one, so
they can be reworded first. The listing is informational and changes nothing.

Datasets under two spellings of the key that differ only in case (`Datasets` and `datasets`) are
refused at save, on workflow save and at render.

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
- The layout is read as the caller reads it too. A layout they cannot read comes back as
  `problem` (`The layout the email template names is not one you can read.`), and its Sensitive
  fields render empty.
- It has its own rate limit, `email-preview`: 30 requests a minute per signed-in user, counted
  after authentication.

There is no test send. A preview never sends.
