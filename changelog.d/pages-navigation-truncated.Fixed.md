- **Pages navigation left out pages past `MaxPages` without saying so.** It read the oldest
  `MaxPages` published pages, so newer pages and everything under them were missing from the menu
  while resolve still served them, and nothing told the renderer. The body now carries `truncated`,
  true when there were more, the way the tree already did. `contract` stays 1, since adding a field
  is not a breaking change.
