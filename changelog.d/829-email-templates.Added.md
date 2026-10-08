- **Email templates as content.** The new `email` blueprint creates `email-template` (subject,
  markdown body, layout, locale) and `email-layout` (header, footer, logo, colours), neither publicly
  deliverable. An Email workflow action can name one with `Template` (id or slug) instead of writing
  `Subject` and `Body`; a workflow without it sends exactly as before, and one with both is refused
  with 400. Only a published template of the workflow's own tenant is sent: a missing, erased,
  draft, archived or scheduled template, a missing or unpublished layout, or a text past 262,144
  characters fails the action permanently, saying which. The body is markdown with raw HTML off,
  placeholders resolve with the same engine and HTML encoding as an inline body, and links keep only
  `http`, `https` and `mailto` addresses. With no layout, the tenant's `site` entry gives the name,
  logo and colours. Saving a workflow warns about the template's placeholders as it does for inline
  text. `POST /api/email-templates/{id}/preview` renders a template against an entry without sending,
  for a caller with `manage_workflows` who can read both, showing the entry as that caller reads it,
  under its own `email-preview` rate limit. Markdown is rendered with Markdig 1.4.0 (#829).
