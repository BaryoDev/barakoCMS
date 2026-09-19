- **`scripts/holdout.sh` decides whether a test added by a pull request notices the change that
  pull request made.** The method has required this since September 5 and nothing ran it: preflight
  had no revert step, no pull request body carried a binding, and the review checklist item that
  reads "mutation reverted and the named tests failed" was being answered yes over a revert that
  never happened. The script reads a fenced `holdout` block naming, for each new test, the
  production hunk it depends on; it holds that hunk out in a throwaway worktree, rebuilds, and
  requires the named test to fail, then restores and requires it to pass again. A hunk that is
  bound to no test and not listed under `untested:` with a reason fails the run, so an omission has
  to be written down rather than simply left out. A held-out tree that does not build is
  inconclusive rather than a pass, because a revert that breaks compilation would otherwise read as
  a test failing for the right reason forever. Exit codes are distinct (0 pass, 1 caught,
  2 unresolved, 3 inconclusive) so a caller cannot collapse them into "non-zero, whatever".
  `scripts/testdata/holdout/run-fixtures.sh` runs a known one-hunk change past all eight cases,
  every failure path included, and preflight fails if any of them stops producing its exit code.
