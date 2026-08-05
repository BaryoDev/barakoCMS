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

`dotnet list package --vulnerable` is clean and gated in CI. On the npm side, `admin/` carries three
High advisories that have no upstream fix, all rooted in the pinned `next` release:

| Package   | Why it is not fixed                                                              |
| --------- | -------------------------------------------------------------------------------- |
| `next`    | Upgrading past the current pin introduces a redirect regression (51 e2e failures) |
| `postcss` | Nested inside `next`; build-time only, runs on this app's own trusted source      |
| `sharp`   | Nested inside `next`; Next's peer range excludes the patched release              |

Exposure is narrower than the raw advisory list suggests: the admin declares no `middleware`, no
Server Actions, and no `next/image`, so the middleware/proxy-bypass, Server-Action CSRF, and image
optimizer/`sharp` advisories do not apply to it. The residual is denial-of-service and cache-poisoning
on server-rendered paths, behind authentication. Revisit when Next ships a stable release that both
bumps the nested dependencies and clears the redirect regression.

Note that a raw GitHub Dependabot alert count for this repo overstates the real picture: alerts remain
open for `examples/nextjs-starter`, a scaffold deleted in `7cfa43c`, and for `admin/` packages that
have since been patched. `npm audit` in `admin/` is the accurate signal.
