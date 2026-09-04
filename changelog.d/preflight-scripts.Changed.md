- **`scripts/preflight.sh` and `scripts/sync-master.sh` replace the manual PR checklist.**
  Preflight builds the solution, runs the named test classes, and checks changelog fragments,
  module versions, locked-mode restore, dashes and banned words on the diff, and workflow YAML for
  duplicate keys, failing on the first problem with a one-line reason. Sync-master merges
  `origin/master`, regenerates lock files when a `.csproj` or `Directory.Packages.props` changed
  in the merge, and exits 1 with the conflicting files listed if it did not go clean.
