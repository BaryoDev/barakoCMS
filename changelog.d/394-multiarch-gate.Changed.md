- **Verified #394 rather than assuming it.** `docker manifest inspect` on `barako-cms:3.21.0`,
  `barako-cms-decaf:3.21.0` and `barako-admin:3.21.0` confirms all three are `linux/amd64` only.
  `release.yml`'s platform gate already checks the pushed manifest (not the build config), runs for
  both images this repo builds, blocks `tag-release` on failure, and CI already proves it fails on
  `3.21.0` and passes on `latest` (#510). No workflow hole found. `docs/deploy-in-production.md` now
  also names `barako-admin`, which has the same amd64-only versioned tag but is built and released
  by BaryoDev/barakoBrew, outside this gate.
