# changelog.d

One file per change. `scripts/changelog-assemble.sh` folds them into `CHANGELOG.md` at release time
and deletes them.

## Why

Every pull request used to add its entry to `CHANGELOG.md` directly, so every branch conflicted on
that one file after every merge. During the 4.0 push that was twelve merges, each one making the
remaining branches conflict again, and resolving it by hand at the point of lowest attention went
wrong twice:

- Conflict markers reached master and all eleven checks passed over them, because nothing reads
  Markdown (#390).
- Three entries were pasted back into a file that already held them (#391).

Two branches adding two files do not conflict. That is the whole idea.

## Adding one

Name the file `<slug>.<section>.md`. The slug is yours, an issue number or a short description; the
section has to be one of `Breaking`, `Added`, `Changed`, `Removed`, `Fixed`, `Security`, matching the
headings `CHANGELOG.md` already uses.

```
changelog.d/395.Fixed.md
changelog.d/otp-race.Fixed.md
changelog.d/content-type-removed.Breaking.md
```

Write the entry exactly as it would appear in the changelog, opening with a bolded lead that says
what was wrong, then what changed:

```markdown
- **A fresh deployment's first backup always failed.** `db-backup` started as soon as Postgres was
  healthy and took its proof backup immediately, racing the API creating its tables. It waits for
  the application schema now.
```

`scripts/changelog-assemble.sh --check` validates the fragments without changing anything, and CI
runs it. It checks the section is real, the file is not empty, the lead is bolded, and there are no
em dashes.

## Releasing

Run `bash scripts/changelog-assemble.sh`, read the diff, commit. It is not wired into the release
workflow yet: that workflow publishes fourteen immutable packages and is the last thing worth
destabilising. See #392.
