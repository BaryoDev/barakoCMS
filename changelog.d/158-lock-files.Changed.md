- **NuGet lock files are committed and restores run in locked mode.**
  Every project now writes `packages.lock.json` (`RestorePackagesWithLockFile` in
  `Directory.Build.props`) and the files are committed. CI, the release workflow, both Dockerfiles
  and the upgrade, restore and smoke scripts restore with locked mode on, so a version bump that
  does not carry its lock file diff fails with NU1004 instead of being quietly regenerated. A
  transitive bump is now a reviewable diff, and GitHub attributes the dependency graph to this
  repository. The README gains a "What it runs on" section with the pinned versions, and names
  Umami and Caddy as deployed alongside rather than referenced.
