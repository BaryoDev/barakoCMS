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
| `email-code` | The tenant's policy allows `otp` and an email transport is configured for it. This is what `POST /api/auth/otp/request` needs to deliver a code. |
| any other id | A module registered an `ISignInMethod` with that `Name`. |

A module announces a method with `services.AddSingleton<ISignInMethod>(new MyMethod())`. The `Name`
is the id. It is lower case, and it cannot be `password` or `email-code`.

On the default tenant every announced module method is listed. On any other tenant a module method
is listed only when the tenant's `signIn.methods` names it.

## Stability

The ids are public API and follow the package version (DECISIONS.md D14). An id is not renamed or
removed within a major version. Adding an id is not a breaking change.
