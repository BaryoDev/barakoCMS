# Magic links

Sign in with a link emailed to a registered address. It sits beside sign-in codes (`/api/auth/otp/*`).

## Flow

1. `POST /api/auth/magic-link/request` with `{ "email": "..." }`. The answer is always 200 with the same message, whether or not the address is registered. A new request spends any link still outstanding for that address.
2. The user gets an email with a link to `GET /api/auth/magic-link/confirm?token=...`. The link is built from `App:BaseUrl`. Without it the email carries the bare token for a client to hand back.
3. That GET only shows a confirm button. It never signs in and never spends the link, so a mail scanner or link preview cannot use it.
4. `POST /api/auth/magic-link/confirm` with `{ "token": "..." }` (JSON or form) spends the link and returns the same body as `/api/auth/otp/verify`: access token, refresh token, or the MFA challenge when the account has MFA enrolled.

## Rules

- A link works once and lives 15 minutes. Only a hash of its secret is stored.
- A link signs in to the tenant it was requested in. Another tenant is refused.
- Two POSTs with one link sign in once. The other is refused with the same message as a bad link.
- Both routes share the `auth` rate limit with sign-in codes.
- A tenant whose sign-in methods exclude `otp` refuses links too. A separate `magic-link` method comes with per-tenant sign-in methods.
