- **Collection syncs can treat an empty answer as complete, and refuse a partial body.**
  `emptyIsComplete` (with `archiveMissing`) lets a run that read no items archive every entry the
  sync owns, for a source that really can be empty. `incompleteWhen` (`path` and `equalTo`) fails
  the run before anything is written when a JSON response says its list is partial, such as GitHub
  search's `incomplete_results: true`. Both are optional and off by default, so existing syncs
  behave as before and neither contract number moves. See `docs/collection-syncs.md`.
