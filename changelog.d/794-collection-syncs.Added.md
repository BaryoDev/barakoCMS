- **A collection's entries could only come from somebody typing them, so three sites each carried
  their own code to read NuGet, GitHub, Medium and an RSS feed at build time.** A collection sync is
  configuration now: `/api/collection-syncs` names the content type to fill, the source (a request
  definition sent through its connector, or an RSS or Atom URL), which response path becomes which
  content field, and which field is the stable key, so a re-sync updates an entry rather than adding
  another one. A field can be given a floor, keeping the greater of the stored and the fetched value,
  so a lagging registry index cannot walk a download count backwards. A background sweep runs each
  sync on its own interval, one instance at a time under a Postgres advisory lock, and a response
  that says the same thing writes nothing. A failed fetch leaves the last good entries in place and
  records the reason on the sync, where `GET /api/collection-syncs/{slug}` shows it along with the
  last success, so an operator can tell a stale page from an empty one. Entries are ordinary content,
  so blocks, delivery and search treat them like anything else. The schedule is on by default and
  `CollectionSyncs:Enabled=false` turns it off without disabling the syncs. Additive on both
  surfaces, so `ApiContract.Version` does not move. (#794)
