- **`BarakoCMS.ExternalAuth`, `BarakoCMS.Files` and `BarakoCMS.Portability` go to 4.2.1.** All three
  carried source changes from the 4.2.0 batch while still declaring 4.1.1, so the release pushed them
  with `--skip-duplicate` and dropped them: NuGet has all three at 4.1.1 and none of those changes
  reached anyone. The one that matters is #754, which mints the OAuth state from
  `RandomNumberGenerator` across the Google, GitHub, Facebook and LinkedIn endpoints. That fix is in
  core and in the image since 4.2.0 and is in no package. The others are #748 (Files module
  documentation) and #861 (the content type field cap, in Portability). 4.2.1 rather than 4.2.0,
  because a module's version is the version of the release that publishes it and v4.2.0 is already
  tagged; picking a number whose tag exists is what caused the 4.1.0 skip in #749.
- **`scripts/check-module-versions.sh` could not see this during the 4.2.0 release.** It resolves a
  module's reference point to the `v<version>` tag when one exists, and `v4.2.0` did not exist until
  the release created it, so the check only went red afterwards. It passed on the release pull
  request and failed on the next one, with the same module versions on both. This is the third time
  an unbumped module has swallowed a shipped change (3.12.1, 3.17.1, #749), and the second time it
  was a security fix.
