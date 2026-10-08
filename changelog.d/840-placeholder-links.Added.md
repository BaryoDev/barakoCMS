- **An email from a workflow could not link to the entry it was about.** `{{links.console}}` (and
  `{{links.edit}}`) give the entry's page in the console from the new `App:ConsoleUrl` setting,
  `{{links.transition "Approve"}}` the same page with the transition named, `{{links.entry}}` the
  entry in this API from `App:BaseUrl`, and `{{links.site "/approvals/"}}` a page on the tenant's
  site from the `Url` of its site settings. Links are built from configuration only, carry no
  token, and render empty when their base is not set. Signed one-click approvals are not part of
  this (#840).
