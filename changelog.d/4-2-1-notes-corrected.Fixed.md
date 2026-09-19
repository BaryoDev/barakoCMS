- **The 4.2.1 notes said the 4.2.0 release had skipped three modules. It had not.** The 4.2.0 run
  pushed `BarakoCMS.ExternalAuth`, `BarakoCMS.Files` and `BarakoCMS.Portability` at 4.1.1, a number
  none of them had been published under, so the 4.1.1 packages hold the 4.2.0 work and 4.2.1 is the
  same code under the version the release check expects. The changelog and the GitHub release say
  so now. The 4.2.0 and 4.2.1 sections were written by hand with their fragments left in
  `changelog.d`, so the next assemble would have announced sixty shipped changes as new; the
  entries those sections left out are added to 4.2.0 and 4.1.0, and the fragments are gone. The
  playground deploy runs `db-assert` against the pulled image before it recreates the app, which
  the 4.2.1 notes claimed and nothing did.
