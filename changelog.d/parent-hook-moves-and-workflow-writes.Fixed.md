- **A move could push a page past `MaxDepth`, every edit under a parent queued on one lock, and a
  workflow `UpdateField` skipped lifecycle hooks.** `ParentReferenceHook` counted only a moved
  entry's new ancestors, so moving a page that had children under a chain near the limit was
  accepted and a child's next edit was then refused. A move now also counts the levels below the
  moved entry. A save that keeps the stored parent now returns before the advisory lock and the
  walk, so concurrent edits under one parent no longer wait on each other. `UpdateField` on a data
  field now runs the content type's lifecycle hooks before it writes, stores what a hook adds, and
  fails permanently with the hook's messages when one refuses, so a workflow can no longer store a
  parent loop or anything else the Update endpoint would refuse.
