- **`BarakoCMS.Import` and `BarakoCMS.Portability` 4.1.1 ship the singleton handling written for
  4.1.0 but never published.** Both were bumped to 4.0.1 in #735, a version already on NuGet from
  the 4.0.1 release, and the release pushes with `--skip-duplicate`, so both packages were silently
  skipped and consumers still have the 4.0.1 built before the change. On that package, a bulk
  import of a singleton content type creates every record in the batch, because the validator caps
  a singleton by counting rows already stored and inside one batch there are none yet; a content
  type whose name was stored before names were normalised matches no definition at all, so the
  batch cap is wrong and the public-field set comes out empty; and a Portability import drops the
  singleton flag off the types it writes. The code has been in core and in the image since 4.1.0,
  so only the two standalone packages are affected. Take 4.1.1. The rule the bump missed is that a
  module's version is the version of the release that publishes it, which is why
  `BarakoCMS.Templates` went to 4.1.0 in that same release; 4.0.1 was already tagged and published.
  This is the third time a module version has swallowed a shipped change (see 3.12.1 and 3.17.1),
  and the first time `scripts/check-module-versions.sh` caught it rather than a user reporting it.
