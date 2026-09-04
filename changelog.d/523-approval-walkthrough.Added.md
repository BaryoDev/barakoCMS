- **A page that walks the invoice approval scenario end to end against the API.** A lifecycle per
  type, a permission on a transition, a workflow that fires on one and email from settings were each
  on master and tested, and nothing in `docs/` mentioned any of them. `docs/approval-by-configuration.md`
  declares the invoice type, gives one role create and another the approve transition, shows the
  raiser refused on approve and on their own submit (and the `Lifecycle:AllowSelfTransition` switch),
  attaches an email workflow to the approve transition and sets the sender from settings, one curl
  per step with the status code each answers. `docs/access-control.md` links to it from the
  permissions section.
