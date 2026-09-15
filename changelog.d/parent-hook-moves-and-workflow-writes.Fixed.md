- **A move could push a page past `MaxDepth`, every edit under a parent queued on one lock, and a
  workflow `UpdateField` skipped lifecycle hooks.** `ParentReferenceHook` counted only a moved
  entry's new ancestors, so moving a page that had children under a chain near the limit was
  accepted and a child's next edit was then refused. A move now also counts the levels below the
  moved entry, however the children spell the parent id. A save that keeps its parent now takes the
  lock in shared mode and skips the walk when the committed parent still matches, so concurrent
  edits under one tree no longer wait on each other while a move still waits for them. `UpdateField`
  on a data field now runs the content type's lifecycle hooks before it writes, stores what a hook
  adds, and fails permanently with the hook's messages when one refuses, so a workflow can no longer
  store a parent loop or, for example, change the lines of a posted journal entry.
