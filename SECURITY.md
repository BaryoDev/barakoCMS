# Security Policy

## Reporting Security Vulnerabilities

If you discover a security vulnerability in BarakoCMS, please report it responsibly:

1. **Do NOT** create a public GitHub issue
2. Email security concerns to: arnelirobles@gmail.com
3. Include:
   - Description of the vulnerability
   - Steps to reproduce
   - Potential impact

## Response Timeline

- **Acknowledgment**: Within 48 hours
- **Initial Assessment**: Within 1 week
- **Fix/Resolution**: Depends on severity

## Supported Versions

| Version | Status | Supported until |
| --- | --- | --- |
| 4.x | Actively supported | 5.0 ships, plus 12 months |
| 3.x | Security fixes only | 4.0 ships, plus 12 months |
| 2.x | Not supported | ended |
| < 2.0 | Not supported | ended |

4.0 has not shipped yet. Until it does, 3.x is the current line and is actively supported: the
published package is 3.21.0. The row above is what 3.x becomes on the day 4.0 is tagged, and the
twelve months run from that day.

### What "supported" means

**Actively supported** means security fixes, correctness fixes, and compatible improvements. It is
the line new work lands on.

**Security fixes only** means fixes for vulnerabilities, and nothing else. A correctness bug in a
security-fixes-only line is fixed on the supported line and backported only if it has a security
consequence.

**Not supported** means no fixes of any kind, including for vulnerabilities. Upgrade.

### The rule, so you can plan without asking

A major is actively supported from the day it ships until twelve months after its successor ships.
That gives a full year to move once a new major exists, and it is a rule rather than a date, so it
does not go stale in this table. The 3.x row used to carry a fixed date, worked out from a 4.0 that
was expected in August 2026 and has not shipped, which is exactly the staleness this rule exists to
avoid. Dates go in the release notes for the release that starts the clock, not here.

There is no long-term-support line. If one is ever offered it will be announced as its own
commitment, not implied by this policy.

### Modules

The module packages version independently of the core, and their support follows the core they
target rather than their own version number. A module released against 4.x is supported as long as
4.x is. A module's own major bump does not start a new support window.

### What this is not

This is a maintenance policy for an open-source project, not a service-level agreement. It says what
gets fixed and for how long. It says nothing about response times for your specific issue, and
nothing in it is a contractual commitment. For the vulnerability-response timeline, see the top of
this file.

## Compliance posture

`docs/compliance-posture.md` states what exists, what does not, and what self-hosting moves to the
operator. It is written for a procurement review: SOC 2, ISO 27001 and third-party penetration
testing are all absent, and it says so directly rather than leaving the question open.

## Security Best Practices

When deploying BarakoCMS:

- Never commit `.env` files with real credentials
- Use environment variables for all secrets
- Rotate JWT keys and database passwords regularly
- Enable GitHub secret scanning on forks
- Set a dedicated `Mfa:Key`. It encrypts stored TOTP secrets and falls back to the JWT signing key
  when unset, which couples two controls to one secret. Note it is an **encryption** key, not a
  signing key: rotating it makes existing MFA secrets undecryptable and locks out enrolled users,
  so treat rotation as a migration.
- Set a dedicated `Secrets:Key`. It encrypts credentials an operator entered in the admin, the email
  provider API key today, and falls back to the JWT signing key when unset. The same warning applies
  as for `Mfa:Key`: rotating it makes stored credentials undecryptable, and the recovery is somebody
  typing them in again. They are separate keys so rotating one does not retire the other.
- Enable MFA on admin accounts. Every sign-in path (password, email code, social) honors it.

## Known advisories we accept

None, currently. Both gates are clean: `dotnet list package --vulnerable` and `npm audit` in `admin/`
each report zero, and CI fails the build on a Critical or High finding.

For a while this section listed three High advisories in `next`, `postcss` and `sharp` as unfixable,
because upgrading Next appeared to break 28 end-to-end tests. It did not. Next 16.1 began refusing
cross-origin requests for dev-server assets, and the test suite drives `127.0.0.1` while the dev server
treats `localhost` as its origin, so the chunks were blocked, the app never hydrated, and every test
that clicked something failed. One line of `allowedDevOrigins` in `next.config.ts` fixed it and the
upgrade went through. Worth remembering the next time a dependency looks like it broke the product:
here the product was fine and the harness was misconfigured, and reading it the other way cost weeks of
carrying advisories that had a fix available all along.

Note that a raw GitHub Dependabot alert count for this repo overstates the real picture: alerts remain
open for `examples/nextjs-starter`, a scaffold deleted in `7cfa43c`, and for `admin/` packages that
have since been patched. `npm audit` in `admin/` is the accurate signal.
