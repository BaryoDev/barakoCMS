- **Three things the first real holdout runs found, and `none:` which the check had never supported
  despite its own pull request declaring it.** Running it against a feature branch, a merged bug fix
  and a synthetic one-line change surfaced: the script resolved the repository from its own file
  location, so a copy run from anywhere else reported "cannot find merge base" and read as a git
  problem rather than a path one; a file the change adds outright is a single hunk covering the
  whole file, and holding it out deletes the file, which never compiles, so the run spent two builds
  to reach an inconclusive it could have predicted from the diff; and a class whose every test fails
  on the clean tree is usually a stopped Docker rather than a broken branch, which the message now
  says when the daemon is unreachable. `none: <reason>` is now parsed, and a change declaring it
  while touching production files fails rather than passing, so it cannot be used to opt out. The
  fixture suite covers all four, eleven cases now.
