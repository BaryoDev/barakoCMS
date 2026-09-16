- **`GET /api/contents/by-slug/{type}/{slug}` told a caller that an entry they could not read
  existed.** It answered 404 for a missing slug and 403 for an unreadable entry, and slugs are
  guessable, so any signed-in account, a self-registered one with no content permission included,
  could confirm a draft or another user's entry by its slug. An unreadable entry now answers 404.
  Where a slug is held by more than one entry (saved before uniqueness was enforced in #717, or
  since through a bulk import or a workflow), only the oldest row was checked, so a newer duplicate
  the caller could read was refused; the route now returns the oldest of the ten oldest matching
  rows that the caller can read.
