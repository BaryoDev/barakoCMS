- **The docs named the console image `barako-admin` while everything else said barakoBrew.** The
  README, the quickstart README and `docs/deploy-in-production.md` now point at
  `ghcr.io/baryodev/barako-brew`, which barakoBrew publishes from 1.2.0 (BaryoDev/barakoBrew#84).
  The old name is still pushed at the same digest until barakoBrew 2.0.0. The deployment note on
  `3.21.0` no longer says nothing publishes the console, since barakoBrew does, with its own
  platform check.
