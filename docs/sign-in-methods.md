# Sign-in methods

`GET /api/auth/methods` is anonymous and answers with the ids of the ways in this instance offers:

```json
{ "methods": ["password", "email-code"] }
```

A client draws its sign-in choices from the list. It returns ids only. Labels belong to the client.
Social providers are still listed by `GET /api/auth/providers`.

## Ids

| Id | Listed when |
|---|---|
| `password` | The tenant's sign-in policy allows `password` (always, unless the tenant names methods and leaves it out). |
| `email-code` | The tenant's policy allows `otp` and the registered email provider delivers (it is not the mock). SMTP needs no API key, so a key is not the test. |
| any other id | A module registered an `ISignInMethod` with that `Name`. |

A module announces a method with `services.AddSingleton<ISignInMethod>(new MyMethod())`. The `Name`
is the id. It is lower case. The built-in ids (`password`, `email-code`, `otp`, `social`) are
reserved, and a module announcing one is ignored.

On the default tenant every announced module method is listed. On any other tenant a module method
is listed only when the tenant's `signIn.methods` names it.

## Who can see it

The list reflects the requesting tenant's sign-in policy, and anyone can read it. That is inherent:
the sign-in screen needs it before anyone has signed in. A tenant slug nobody registered gets the
same list as the default tenant. The route uses the delivery rate limit, since a console calls it on
every page load.

## Stability

The ids are public API and follow the package version (DECISIONS.md D14). An id is not renamed or
removed within a major version. Adding an id is not a breaking change.
