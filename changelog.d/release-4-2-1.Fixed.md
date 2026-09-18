- **4.2.1 exists to publish `BarakoCMS.ExternalAuth`, `BarakoCMS.Files` and `BarakoCMS.Portability`.**
  All three carried 4.2.0 source changes while still declaring 4.1.1, and 4.1.1 was already on NuGet
  from the 4.1.0 release, so the 4.2.0 publish pushed them with `--skip-duplicate` and dropped them.
  Core has no changes of its own here: the release gate reads core's `<Version>` alone, so bumping it
  is what lets the modules reach NuGet, the same as 3.12.1, 3.17.1 and #749.
