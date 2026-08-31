<!-- Title format: Area: Description (closes #123)
     e.g. "Auth: Reject expired OTP codes on the second attempt (closes #142)" -->

<!-- The coding standard is CLAUDE.md (CODING_STANDARDS.md points at it). Most review comments here
     are already written down in it. -->

## What changed and why

<!-- What was wrong, and what this does about it. Link the issue. -->

Fixes #

## How to test

<!-- The steps a reviewer runs to see this working. -->

## Which test fails without this change

<!-- Name the test. For a bug fix, a test that passes both with and without the change proves
     nothing: either write it first and watch it fail, or revert the fix and confirm it goes red.
     For docs, build or dependency changes, write "not applicable". -->

## Checklist

- [ ] A test covers this, and I confirmed it fails without the change
- [ ] `dotnet build` is clean, with no new warnings
- [ ] Public API changes keep existing consumers compiling, or the break is called out below
- [ ] Docs updated if behaviour or configuration changed

## Anything reviewers should know

<!-- Trade-offs, things deliberately left out, follow-up issues filed. Leave blank if nothing. -->
