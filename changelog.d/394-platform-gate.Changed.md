- **The image platform gate had never been seen to fail.** The release workflow refused to publish
  an image that did not serve both `linux/amd64` and `linux/arm64`, but the check had only ever
  passed, and 3.21.0 (amd64 only) predates it. The assertion is now `scripts/check-image-platforms.sh`,
  which `release.yml` calls, and CI runs it against `barako-cms:3.21.0` and passes only when the
  script refuses that tag for being amd64 only, then against `latest` and passes only when it
  accepts it. The versioned tags themselves stay amd64 only until the next release publishes
  through the gate.
