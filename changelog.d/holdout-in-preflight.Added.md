- **Preflight now runs holdout on the change in hand, not just on its fixtures.** Until now the
  fixtures proved holdout still worked and nothing ran it against the pull request being prepared,
  so the check existed and was never asked anything. `--body <file>` points it at the pull request
  body. It is required when the diff touches production code and ignored when it does not, because
  a release, a changelog or a docs pass has no hunk to bind and demanding a declaration there is
  friction that buys nothing. A change that touches production with no `--body` fails rather than
  skipping: a check that quietly does nothing when its input is missing is the hole this script
  keeps closing one level at a time. Each holdout exit code maps to its own message, so a caught
  test, an unresolved binding and an inconclusive build do not collapse into one failure.
