- **A tenant could not require MFA, or keep its sessions short, on its own.** Whether a second
  factor was needed came from the account or its roles in every tenant alike, and every refresh
  token lived seven days. A tenant now has `signIn` rules on `PUT /api/tenants/{handle}`:
  `requireMfa` for every member, `mfaRoleIds` for members holding chosen roles there, `methods` to
  allow only some of password, sign-in code and social sign-in, and `sessionMinutes` to shorten the
  refresh lifetime, and the access token when shorter than fifteen minutes. The issuer decides all
  but the methods, so login, codes, MFA, social sign-in, refresh and tenant switch agree, and the
  strictest of the role and the tenant applies. `TokenIssueResult.RefreshLifetime` carries the
  length to every path that mints a refresh token. ExternalAuth 4.6.0 (#920).
