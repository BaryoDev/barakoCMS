- **Holdout asked for 245 declarations on a refactor that had no bindings to make, and its
  production list counted files no test can be bound to.** Found by pointing it at the assembly
  split in #971: the unfiltered list named 286 files, 41 of them `.csproj` and `packages.lock.json`,
  which have no behaviour to hold out and change on every dependency bump. Build metadata is
  excluded now, as are pure renames, which have no hunk at all. The two copies of that file list,
  one for `none:` and one for the unclaimed check, are one definition, so they cannot drift apart.
  `none:` is also accepted when the change adds or edits no test file: there are no new tests, so
  there are no bindings to make. It cannot be used to dodge one, because touching a single test
  makes it fail again, and that is exactly the change for which a binding is owed. The guard was
  written with `--diff-filter=ad` copied from the production list, which excludes added paths and
  so could never notice an added test; the fixture caught it before it shipped.
