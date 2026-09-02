# AGENTS.md

Working agreement for AI coding agents on BarakoCMS.

**The rules live in [CLAUDE.md](CLAUDE.md). Read it before changing anything.** This file exists
because different tools look for different filenames; the content is not duplicated here, so there
is one source of truth rather than three that drift.

| Tool | Reads |
|---|---|
| Claude Code | `CLAUDE.md` (automatically), plus `.claude/settings.json` and `.claude/hooks/` |
| Codex, Jules, Amp and others following the AGENTS.md convention | this file |
| Cursor | `.cursorrules` |
| A person looking for the coding standard by name | `CODING_STANDARDS.md`, which points here |

## The short version

Full detail is in `CLAUDE.md`. The rules people break most often:

1. **A bug fix ships with a test that failed before the fix.** Write it first, or revert your change
   and confirm the test goes red. A test that passes both ways proves nothing.
2. **Package versions go in `Directory.Packages.props`, never in a `.csproj`.** Reference packages
   without a version. No floating versions such as `3.7.*`.
3. **Shared MSBuild settings go in `Directory.Build.props`.** Don't repeat target framework,
   nullability, licence or company metadata per project.
4. **No AI attribution in commits or pull requests.** No `Co-Authored-By`, no "Generated with"
   footer.
5. **Default to no comment.** Explain a non-obvious *why*, not what the code already says. Never
   leave `// fix for X` or `// see PR #123`.
6. **Integration tests need Docker.** `DockerUnavailableException` across many tests means Docker
   is not running, not that you broke something.
7. **Public API is compiled against by other people.** Within a major version, add an overload and
   obsolete the old member rather than changing a signature.

## Enforcement

Rules 2 and 3 are checked automatically by `.claude/hooks/check-project-file.sh`, and rule 4 by
`.claude/hooks/check-commit-message.sh`, both wired up in `.claude/settings.json`. Inline package
versions and floating versions are blocked outright; a project-level override of a shared property
is allowed but flagged, since a project occasionally has a real reason.

If a hook blocks you and you believe it is wrong, say so in the pull request rather than working
around it. The hooks encode decisions, and a decision that no longer holds should be changed in
the open.

## Changing the rules

`CLAUDE.md`, `.claude/**`, `AGENTS.md` and the shared build files are covered by `CODEOWNERS`, so
changes there need review. That is deliberate: hook scripts execute on every contributor's machine,
so a change to them is a change to what runs locally, not just to what the code does.
