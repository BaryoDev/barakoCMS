- **A delivered reference names only entries delivery would serve.** On the anonymous routes (the
  list, search, the read by slug and its preview, the feed, the event stream, share links and the
  tenant profile) and in `IPublicContentProjector.ProjectAsync`, a reference field keeps an id only
  when it names an entry in the same tenant that is Published, document Public and of a publicly
  deliverable type, the test `include` already applied. A single reference to any other entry is
  left out of the entry, and a list keeps the ids that pass, in stored order. An entry resolved
  through `include` has its own references checked the same way. The check reads the targets once
  per response. On the list and search, a `filter[field][eq|ne|has]` on a reference field that
  names any other entry answers as if no entry held that id, which is what the delivered data
  shows. If the event stream cannot read the targets, it sends the commit's changes with no
  reference ids in them and still sends its unpublish events.
