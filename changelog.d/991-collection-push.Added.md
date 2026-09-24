- **A collection could be pulled from a source on a schedule but not pushed to by the source.**
  `POST /api/collections/{type}/push` upserts entries by slug in one transaction, so a repository's
  CI can push its changelog, contributors or docs when they change. Every entry goes through the
  same permission, sensitivity, schema and lifecycle checks as the content API, an unchanged entry
  writes nothing and fires no webhook, and `archiveMissing` archives published entries the push
  left out once every write has passed. The response counts created, updated, unchanged and
  archived entries and lists refused ones. At most 1,000 entries and 4 MB per push. API keys take
  an optional `contentTypes` list, which limits a key to pushing to those types and nothing else;
  existing keys name none and are unchanged. See `docs/collection-push.md`.
