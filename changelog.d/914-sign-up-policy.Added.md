- **A person who registered on a tenant could not then sign in to it.** Registration, social
  sign-in and invitations each decided for themselves who may join, none asked the tenant, and none
  wrote a membership, so the issuer refused the new account a token. A tenant now has a sign-up
  policy, `signUp` on `PUT /api/tenants/{handle}`: `Open` with a `signUpRoleId`, `InviteOnly` or
  `Closed`. An Open tenant's registration and social sign-in create the membership with that role;
  InviteOnly and Closed refuse registration with 403 and create no account through social sign-in;
  Closed also refuses an invitation that would create an account. A tenant with no policy, and the
  default tenant, behave as before. `IEmailVerificationService` gains an overload carrying the
  tenant, with a default implementation, and `SignUpPolicy` is a new public type the ExternalAuth
  module calls. ExternalAuth 4.6.0 (#914).
