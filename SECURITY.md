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
| 2.x     | ✅ Actively supported  |
| 1.x     | ⚠️ Security fixes only |
| < 1.0   | ❌ Not supported       |

## Security Best Practices

When deploying BarakoCMS:

- Never commit `.env` files with real credentials
- Use environment variables for all secrets
- Rotate JWT keys and database passwords regularly
- Enable GitHub secret scanning on forks
