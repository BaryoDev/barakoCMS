<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.ExternalAuth logo" />
  <h1>BarakoCMS.ExternalAuth</h1>
  <p><em>Continue with Google, GitHub, Facebook, LinkedIn or any OpenID Connect provider.</em></p>
</div>

---

OAuth and OpenID Connect sign-in for barakoCMS. A user arriving through a provider is matched to a
global user by **verified** email the first time, and by the provider's own account id after that, and issued exactly the same tenant-scoped, device-bound token as the built-in flows. Social
sign-in is another way in, not a second, weaker way in.

## Enable it

```sh
dotnet add package BarakoCMS.ExternalAuth
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=ExternalAuth`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.ExternalAuth.ExternalAuthModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


## Endpoints

| Method & path | Purpose |
|---|---|
| `GET /api/auth/providers` | Which providers are configured, for rendering buttons |
| `GET /api/auth/{provider}/start` | Begin the OAuth handshake |
| `GET /api/auth/{provider}/callback` | Provider redirect target |
| `GET /api/auth/oidc/{name}/start` | Begin sign-in with a provider configured under `Oidc:Providers` |
| `GET /api/auth/oidc/{name}/callback` | That provider's redirect target |
| `POST /api/auth/oidc/{name}/callback` | The same, for a provider set to `ResponseMode` `form_post` (Apple) |
| `POST /api/auth/oidc/{name}/id-token` | A native app exchanges the provider's id token for a barako token |
| `GET /api/me/profile` | Profile details captured from the provider |

`{provider}` is `google`, `github`, `facebook` or `linkedin`. Only configured providers are
advertised or accepted.

`GET /api/auth/providers` answers with a boolean for each of those four and an `oidc` array, one
`{ "name", "displayName" }` for each OpenID Connect provider that is on. The array holds at most 20.

## Configuration

```json
{
  "Google": { "ClientId": "...", "ClientSecret": "..." },
  "GitHub": { "ClientId": "...", "ClientSecret": "..." },
  "LinkedIn": { "ClientId": "...", "ClientSecret": "..." },
  "Facebook": { "AppId": "...", "AppSecret": "..." }
}
```

Each provider is its own section at the root of the configuration, not under `ExternalAuth`. Omit a
provider to leave it disabled. `"Enabled": false` inside a provider's section turns that provider
off while keeping its keys, and `ExternalAuth:Enabled` set to `false` turns every provider off.

## OpenID Connect providers

Any provider that publishes a discovery document can be added by configuration: Keycloak, Auth0,
Cognito, Microsoft Entra ID and others.

```json
{
  "Oidc": {
    "Providers": {
      "keycloak": {
        "Authority": "https://id.example.com/realms/main",
        "ClientId": "...",
        "ClientSecret": "...",
        "DisplayName": "Company sign-in"
      }
    }
  }
}
```

| Setting | Meaning |
| --- | --- |
| `Authority` | The issuer URL. It must be https. `/.well-known/openid-configuration` is fetched from it. |
| `ClientId`, `ClientSecret` | Both required, unless `SignedClientSecret` stands in for the secret. A provider with no client id is off; one with no secret is off and a startup warning says so. |
| `SignedClientSecret` | Optional. `KeyId`, `TeamId` and `PrivateKey` (a P-256 key in PEM), and optionally `Audience`. The client secret is then an ES256 JWT made from them. See Apple below. |
| `ResponseMode` | Optional. `query` (the default) or `form_post`, for a provider that posts the code back. |
| `IdTokenAudiences` | Optional. The audiences the id token grant accepts, as a list or one comma-separated value. The grant is off without it. See below. |
| `EmailVerifiedMayBeText` | Optional. `true` also takes the text `"true"` for the verified claim. For Apple only. |
| `DisplayName` | Optional. What `/api/auth/providers` reports for the button. Defaults to the name. |
| `Scopes` | Optional. Defaults to `openid email profile`. `openid` is added if it is left out. |
| `Issuer` | Optional. What the discovery document's `issuer` has to equal, when that is not the authority. See Microsoft below. |
| `EmailVerifiedClaim` | Optional. The id token claim that says the provider vouches for the email. Defaults to `email_verified`. It must be a claim only the provider sets, never a profile attribute a user can edit. Only the JSON boolean `true` counts, unless `EmailVerifiedMayBeText` is set. |
| `Enabled` | `false` turns the provider off and keeps its keys. |

The name is the key under `Providers`: 1 to 32 characters of `a-z`, `0-9` and hyphen. It is the
`{name}` in the routes. Register `{App:BaseUrl}/api/auth/oidc/{name}/callback` as the redirect URI
at the provider. At most 20 providers are read.

The issuer is compared exactly, so write `Authority` the way the provider's discovery document
writes its `issuer`, trailing slash included (Auth0 has one).

What the flow checks:

- **State, nonce and PKCE.** The start mints all three and keeps them in one HttpOnly, Secure
  `__Host-` cookie for ten minutes. The callback needs the returned `state` to match, sends the PKCE
  verifier with the code, and needs the id token's `nonce` to match. A state is accepted once: the
  cookie is expired by every callback, and the instance that saw a state refuses to see it again
  for those ten minutes. That memory is per instance, so behind several instances a replay that
  reaches another one is refused by the provider, which redeems a code once. It holds 10,000
  states; when that many are all still live it is cleared whole, and until it fills again a replay
  is likewise left to the provider. The cookie holds the three values in clear and is not signed:
  it is a double-submit cookie, and what protects it is the `__Host-` prefix with HttpOnly, Secure
  and SameSite=Lax. For a `form_post` provider it is SameSite=None instead, because a browser
  does not send a Lax cookie on the provider's cross-site POST. The state check is the same: a
  third site can post a code and a state, but it cannot read or set this cookie, so the state it
  posts matches nothing.
- **The id token.** Signed with one of `RS256`, `RS384`, `RS512`, `PS256`, `PS384`, `PS512`,
  `ES256`, `ES384` or `ES512`. `none` and the HMAC algorithms are refused. The token's `kid`
  has to name exactly one key in the provider's key set, and the signature is checked against that
  key only. A token with no `kid` is accepted only when the provider publishes a single key and
  its issuer is not a template. `iss` equals the configured issuer exactly, `aud` contains the
  client id, with more than one audience `azp` has to be the client id, and `exp` and `nbf` hold
  with 60 seconds of skew. `picture` is kept only when it is an https URL.
- **Outbound calls.** The discovery document, the keys and the code exchange go through the same
  guarded HTTP client the core uses for webhooks: https only, no redirects, and no loopback,
  private, link-local or metadata address. Each call has a 10 second timeout. Discovery and keys are
  cached for an hour, a key id the cache does not hold refetches the keys at most once in five
  minutes, and a failed fetch is not retried for 30 seconds. If the key endpoint cannot be reached
  when the hour is up, the keys already held go on being used until they are four hours old. After
  that every token is refused until the keys can be fetched again. Discovery has no such grace: it
  fails at the hour.
- **Where the browser goes.** The redirect URI is `App:BaseUrl` plus the route. Nothing in the
  request changes it, and after sign-in the browser is sent to `App:BaseUrl` only.
- **Rate limit.** Start, both callbacks and the id token grant share 20 requests per five minutes
  per client address.
  `Oidc:RateLimit:PermitLimit` and `Oidc:RateLimit:WindowSeconds` change it.

Only the id token is read. The userinfo endpoint is not called, so a provider that puts the email
only there signs nobody new in. The token endpoint must accept `client_secret_post` or
`client_secret_basic`.

### Which account a sign-in belongs to

A provider account is identified by issuer and subject, never by email alone. The pair is stored
the first time (`mt_doc_external_identities`), and every later sign-in with that pair is the same
user, whatever email the token then carries.

The first time, the pair has to be tied to a user, and that is done by email only when the provider
asserts the address is verified. Then it is linked to the user who holds that address, or to a new
user. When the claim is absent or false the sign-in is refused, nothing is stored, and the person is
told to use an email code.

So every provider you configure can sign in as any local account whose address it asserts as
verified, a SuperAdmin included. Configure only providers you trust to verify email, and remember
that a provider you run yourself (a Keycloak realm, for instance) is as trustworthy as whoever can
edit its users. Two providers that assert the same address land on the same account. An account
with MFA enrolled is still asked for its second factor.

### Linked accounts

A platform administrator (capability `manage_users`, SuperAdmin) lists a user's provider links with
`GET /api/users/{userId}/external-identities` and removes one with
`DELETE /api/users/{userId}/external-identities/{id}`. A tenant administrator cannot: a link belongs
to the person and works in every tenant they sign in to. An unknown user, or a link that belongs to
another user, answers 404. The provider's subject is not returned. The next sign-in through that
provider links again by verified email.

Google, Facebook, GitHub and LinkedIn work the same way. Their account id is the Google and LinkedIn
`sub`, the Facebook `id` and the GitHub numeric id, and the issuer of the link is a fixed name:
`oauth:google`, `oauth:facebook`, `oauth:github`, `oauth:linkedin`. An OpenID Connect issuer is always
an https URL, so the two kinds of link cannot collide. A person whose address changes at the provider
signs in to the same local account. A user who signed in before this existed has no link, so the
first sign-in after the upgrade matches by verified email, under each provider's rule in the table
below, and records the link.

### Choosing providers per tenant

Providers and their client ids stay one set for the deployment. A tenant can narrow which of them it
offers: `PUT /api/tenants/external-providers` with `{ "providers": ["oauth:google", "oidc:azure"] }`
(`GET` and `DELETE` read and remove it, gated like the role map, and each change is audited). A key is
`oauth:google`, `oauth:facebook`, `oauth:github`, `oauth:linkedin`, or `oidc:` and the name of a
configured OpenID Connect provider. A tenant with no list uses every provider the deployment has
configured. A provider the tenant leaves out answers 404 at `/start`, at the callback and at the
`id-token` grant, the same as a provider that is off for the deployment, and
`GET /api/auth/providers?club={handle}` reports only what that tenant allows. The list is stored in
`mt_doc_external_provider_settings`, created by `migrations/5.5.0/915-external-auth-provider-settings.sql`.

### Mapping a provider claim to roles

A tenant sets one map with `PUT /api/tenants/external-role-map` (`GET` and `DELETE` read and remove
it, and each change is written to the audit log). The map is one claim name from the id token, for
example `groups`, and rules from a claim value, read from one provider, to a role:

```json
{ "claim": "groups", "rules": [ { "provider": "azure", "value": "staff", "roleId": "<role id>" } ] }
```

`provider` is the name under `Oidc:Providers` and must be configured, or one of `oauth:google` and
the other three OAuth keys. Only Google and LinkedIn send claims a rule can read (Google's `hd`, the
hosted domain, is the useful one), since GitHub and Facebook report only fields the person edits. A rule only matches a token
that came through that provider, so the same group name at another provider gives nothing. Values
match exactly and case sensitively, and a value cannot start or end with a space. A rule cannot read
`name`, `given_name`, `family_name`, `nickname`, `picture`, `preferred_username`, `email`,
`email_verified`, `sub`, `iss`, `aud`, `phone_number`, `address`, `website`, `profile`,
`middle_name`, `gender`, `birthdate`, `locale`, `zoneinfo` or `updated_at`. A map stored some other
way on one of them is ignored at sign-in.

A rule can also carry `issuer`, which must equal the token's issuer exactly. It is required when the
provider's issuer is a template, as with Entra `common` or `organizations`: there the provider name
matches a token from any directory, and any directory's administrator could assign themselves a
value the rule names. Give the issuer with the directory filled in, such as
`https://login.microsoftonline.com/<tenant id>/v2.0`. The PUT answers 400 without it.

On every OpenID Connect sign-in for that tenant, each rule the token matches adds its role to the
person's membership, creating the membership if there is none. A person with a second factor
enrolled is given nothing at first factor. The roles the map earned travel in the signed MFA
challenge, and `/api/auth/mfa/verify` grants them once the code passes, after checking them again
and that the membership is still Active. A wrong code, an expired challenge or a tampered one grants
nothing. The grant commits with the outcome of
the sign-in, the spent nonce included, or not at all. It only adds. A removed or suspended member is
not brought back, and a claim that stops being sent takes nothing away. Taking a mapped role away
means removing it from the member by hand, and the map adds it back at their next sign-in unless the
map is edited too. The map is read from the sign-in's tenant, never
from the request.

It cannot name SuperAdmin, a role carrying a platform capability, a role of another tenant, or a
role that does not exist; the PUT answers 400, and a map stored some other way is filtered the same
at sign-in. A map can create a membership in a tenant whose sign-up is Closed, for a person who
already has an account: Closed stops people signing themselves up, and the map is the
administrator's own rule.

What is read from the token is bounded. Only top-level claims that are a string or an array of
strings count, at most 64 claims, 100 values for each, and a value longer than 255 characters is
dropped. Nested claims, such as a Keycloak `realm_access.roles`, are not supported; ask the provider
to send a top-level claim. The table is created by `migrations/5.1.0/915-external-auth-role-maps.sql`.

An upgraded database needs `migrations/4.6.0/external-auth-identities.sql` before the new build
starts, like any new table.

### Microsoft Entra ID

One directory is plain configuration: `Authority` is
`https://login.microsoftonline.com/{directory id}/v2.0`.

For the multi-directory endpoints (`common`, `organizations`) the discovery document's issuer is a
template, `https://login.microsoftonline.com/{tenantid}/v2.0`, and each token's `iss` names its own
directory. Set `Issuer` to that template, written with the literal `{tenantid}`:

```json
"microsoft": {
  "Authority": "https://login.microsoftonline.com/common/v2.0",
  "Issuer": "https://login.microsoftonline.com/{tenantid}/v2.0",
  "ClientId": "...",
  "ClientSecret": "...",
  "EmailVerifiedClaim": "xms_edov"
}
```

The token's `tid` has to be a GUID, `iss` has to equal the template with `tid` in place of
`{tenantid}`, and a signing key that names an issuer has to name that same one. The account is
stored under the directory's own issuer, so the same subject in two directories is two people. Any
directory can sign in through `common`; use the one-directory authority to accept only yours.

Microsoft does not send `email_verified`. `xms_edov` is its optional claim for an address whose
domain owner is verified; add it, and the `email` claim, to the app registration's id token.
Without it a first sign-in is refused as unverified.

This is tested against a stub that publishes a template issuer, not against Microsoft.

### Apple

```json
"apple": {
  "Authority": "https://appleid.apple.com",
  "ClientId": "com.example.web",
  "Scopes": "openid name email",
  "ResponseMode": "form_post",
  "EmailVerifiedMayBeText": true,
  "SignedClientSecret": {
    "KeyId": "ABC123DEFG",
    "TeamId": "TEAM456789",
    "PrivateKey": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----"
  }
}
```

`ClientId` is the Services ID. Register `{App:BaseUrl}/api/auth/oidc/apple/callback` as its return
URL. Apple posts the code back as a form when the name or email scope is asked for, so
`ResponseMode` is `form_post`, and the callback is then a POST as well as a GET.

Apple takes no fixed client secret. `PrivateKey` is the `.p8` key from the developer account, as
downloaded; written on one line, `\n` is read as a line break, which is how an environment variable
can hold it. The secret sent with the code exchange is an ES256 JWT with `kid` the key id, `iss` the
team, `sub` the client id and `aud` the issuer (`Audience` changes that). It lives 30 days, Apple's
limit being six months, and a new one is made when a day is left. A key that is not a P-256 private
key turns the provider off with a startup warning, rather than sending no secret.

Apple documents `email_verified` as a boolean or the text `"true"`, so `EmailVerifiedMayBeText`
takes both. Leave it off for other providers. Apple posts the person's name only on the first
sign-in, in a `user` field; it is not read, and the id token carries no name.

This is tested against a stub shaped like Apple, not against Apple.

### Native apps: the id token grant

A MAUI or mobile app that signs in with the provider's own SDK already holds an id token. It
exchanges it here, with no browser:

```http
POST /api/auth/oidc/{name}/id-token
{ "idToken": "...", "nonce": "...", "club": "optional-club" }
```

The answer is `{ "token", "refreshToken", "requiresMfa", "mfaChallengeToken" }`: the same tokens
the callback issues, or an MFA challenge to finish at `/api/auth/mfa/verify`.

The grant is off for a provider until `IdTokenAudiences` lists the client ids its tokens may be
issued to. A native app's client id differs from the web one, so the list replaces `ClientId` here
rather than adding to it. With several audiences in a token, `azp` has to be in the list too.

Google's native tokens name the server's (web) client as `aud` and the app's client as `azp`, so for
Google list both the web client id and each app's. A token the web sign-in was issued also has the
web client as `aud`, with `azp` the web client or absent, and must not be exchanged here. So when a
token's `aud` is the provider's `ClientId`, the grant also needs `azp` to be a listed client that is
not the `ClientId`.

The token is checked as the callback checks it: issuer, signature against the issuer's keys,
audience, expiry, `email_verified`. The `nonce` is required: the app makes a random one, gives it
to the SDK, and sends the same value here, which has to equal the token's `nonce` claim exactly. If
the SDK takes a hash of the nonce (Apple's iOS SDK does), send what the token carries. Each nonce
works once. It is recorded (`mt_doc_oidc_used_nonces`) in the same commit as the sign-in and
refused until the token it came in has expired, so a token that leaks from the app cannot be
exchanged again, while a sign-in that fails (a 409, a transient error) leaves the token usable for
a retry. A 403 or an MFA challenge does spend it. The nonce is queued only for the commit that
records the outcome, so an earlier commit (trusting the device, with the DeviceTrust module on)
does not spend it. A token that expires more than a day ahead is
refused, which bounds how long a record is kept. Expired records are deleted at most every five
minutes, up to 5,000 at a time.
| Answer | When |
| --- | --- |
| 200 | Signed in, or `requiresMfa` |
| 400 | No id token, or a nonce that is not 16 to 256 printable characters without spaces |
| 401 | The token is refused, the nonce was already used, or the email is not verified |
| 403 | Not a member of the club asked for |
| 404 | No such provider, or its `IdTokenAudiences` is empty |
| 409 | Two first sign-ins for one address raced; try again |
| 503 | The provider's discovery document cannot be fetched |

An upgraded database needs `migrations/4.8.0/external-auth-used-nonces.sql` before the new build
starts.

## Security notes

- Matching is on a **verified** email only. Matching on an unverified one would let anyone who can
  claim an address at a provider take over the matching account. Take **0.4.0 or later**: this was
  the documented intent from the start and the enforcement was missing, so Google, LinkedIn and
  Facebook never read a verification flag, and GitHub read one only for accounts whose address was
  private on their profile.

  What each provider is asked for now:

  | Provider | Source of truth | Behaviour |
  | --- | --- | --- |
  | Google | `email_verified` on the OIDC userinfo response | Refused when absent or false |
  | LinkedIn | `email_verified` on `/userinfo` | Refused when absent or false |
  | GitHub | `verified` on `/user/emails` | Only the verified primary is used; the unflagged profile email is ignored |
  | Facebook | none exists | Refused unless `Facebook:TrustUnverifiedEmail` is set |
  | OpenID Connect | `email_verified` in the id token, or the claim named by `EmailVerifiedClaim` | A first sign-in is refused when absent or false. A provider account already linked by issuer and subject signs in without it |

- **Facebook is opt-in.** The Graph API exposes no per-field verification flag, so there is nothing
  to check and no honest way to claim the address is verified. Setting
  `Facebook:TrustUnverifiedEmail` to `true` says you have decided Facebook's own verification is
  good enough for your deployment, and accepts that a Facebook account asserting an address becomes
  a login for the local account holding it. It defaults to off, which refuses the sign-in.
- If the account has MFA enrolled, the provider callback issues an MFA challenge rather than a
  session token. Take **0.1.6 or later**: earlier versions minted a token directly, so a
  provider-account takeover skipped the second factor entirely.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under [MIT](LICENSE).

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
