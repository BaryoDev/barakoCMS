- **`scripts/preflight.sh`, `scripts/sync-master.sh` and `scripts/needs-review.sh` replace the
  manual PR checklist.** Preflight does a locked-mode restore first, before any build, then builds
  with `--no-restore`, runs the named test classes and fails if a class matches zero tests, then
  checks changelog fragments, module versions, and dashes/banned words and workflow YAML for
  duplicate keys, both scans covering untracked files too, failing on the first problem with a
  one-line reason. Sync-master merges `origin/master`, regenerates lock files when a `.csproj` or
  `Directory.Packages.props` changed in the merge, and exits 1 naming either the conflicting files
  or a dirty working tree, whichever blocked it. Needs-review is advisory only: it always exits 0
  and prints one line per rule the diff against `origin/master` fires, for a reviewer to read.
