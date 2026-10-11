- **The stale pull request refresher needed a token made by hand.** It now updates a branch with
  `GITHUB_TOKEN` and then starts `ci.yml` on it through `workflow_dispatch`, since a push made with
  that token starts no workflow. The checks report on the updated head under their usual names.
  `REFRESH_PR_TOKEN` is no longer read and can be deleted. A branch whose update would bring in a
  change under `.github/workflows/` is reported as needing a hand, because `GITHUB_TOKEN` may not
  write there.
