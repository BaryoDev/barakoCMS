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

| Version | Supported             |
| ------- | --------------------- |
| 3.x     | ✅ Actively supported  |
| 2.x     | ⚠️ Security fixes only |
| < 2.0   | ❌ Not supported       |

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
- Enable MFA on admin accounts. Every sign-in path (password, email code, social) honors it.

## Known advisories we accept

None, currently. Both gates are clean: `dotnet list package --vulnerable` and `npm audit` in `admin/`
each report zero, and CI fails the build on a Critical or High finding.

For a while this section listed three High advisories in `next`, `postcss` and `sharp` as unfixable,
because upgrading Next appeared to break 28 end-to-end tests. It did not. Next 16.1 began refusing
cross-origin requests for dev-server assets, and the test suite drives `127.0.0.1` while the dev server
treats `localhost` as its origin — so the chunks were blocked, the app never hydrated, and every test
that clicked something failed. One line of `allowedDevOrigins` in `next.config.ts` fixed it and the
upgrade went through. Worth remembering the next time a dependency looks like it broke the product:
here the product was fine and the harness was misconfigured, and reading it the other way cost weeks of
carrying advisories that had a fix available all along.

Note that a raw GitHub Dependabot alert count for this repo overstates the real picture: alerts remain
open for `examples/nextjs-starter`, a scaffold deleted in `7cfa43c`, and for `admin/` packages that
have since been patched. `npm audit` in `admin/` is the accurate signal.
