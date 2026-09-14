- **The page tree now registers the parent check.** With the Pages module enabled, the configured page
  type refuses a page that is its own parent or closes a loop, which `ParentReferenceHook` made
  possible but nothing registered. A create is walked too, so a new page cannot be hung deeper than
  `MaxDepth` or under a loop already stored.
