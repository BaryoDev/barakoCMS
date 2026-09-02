# Coding standards

**The coding standard for this repository is [`CLAUDE.md`](CLAUDE.md).** Read it before changing
code. It covers the layout, the vertical-slice architecture, build and dependency rules, testing
discipline, public API stability, the comment policy and security.

[`CONTRIBUTING.md`](CONTRIBUTING.md) covers everything around a contribution: claiming an issue,
branch and PR naming, the contributor terms, and what to do when you disagree with a reviewer.

## Why the standard is in a file named after an AI tool

Claude Code, Cursor and the tools following the `AGENTS.md` convention each read a fixed filename,
and they read it automatically. Keeping the standard in `CLAUDE.md` means the agents working in this
repository are held to the same rules as the people, from one source rather than from a copy that
drifts. `AGENTS.md` and `.cursorrules` point at it for the same reason.

The cost is that a human contributor has no reason to open a file named after an AI tool, so the
standard was written, enforced in review, and effectively invisible to the people it applies to.
This file exists so that looking for the coding standard finds it. It is a signpost and deliberately
holds no rules of its own: two copies of a standard means one of them is wrong.
