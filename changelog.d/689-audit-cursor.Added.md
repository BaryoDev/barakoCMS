- **`GET /api/audit` takes a `since` cursor an outside system can poll.** Pass a timestamp the first
  time and the `cursor` of each response after that. Entries come back oldest first in bounded pages,
  and the cursor resumes exactly where the last page stopped. The same masking and tenant rules as
  the list apply. Without `since` the list is unchanged. This is the pull half of #689; a push
  transport is a later topic on the messaging transport, not a second package.
