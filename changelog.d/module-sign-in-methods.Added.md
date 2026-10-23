- **A module can add a tenant sign-in method that is off until named.** A module registers an
  `ISignInMethod`, and a tenant can then name it in `signIn.methods`. An empty list still allows only
  the three built-in methods, so a module method stays off for every tenant that does not name it.
  `TenantSignIn.AllowsExplicitlyAsync` is how a module asks.
