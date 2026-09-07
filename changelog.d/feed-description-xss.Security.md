- **The RSS feed passed authored HTML through unescaped.** A feed item's description wrapped the
  field value in a CDATA block, and many readers render a description as HTML, so a Body of
  `<img src=x onerror=...>` became stored XSS in every subscriber's reader. The description is now
  entity-encoded like the title, so authored markup shows as text and never executes.
