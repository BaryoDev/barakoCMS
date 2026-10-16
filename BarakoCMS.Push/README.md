# BarakoCMS.Push

Web Push for barakoCMS. Stores a push subscription per user and device, sends notifications signed
with VAPID, and deletes a subscription when the push service answers 404 or 410.

## Install

```sh
dotnet add package BarakoCMS.Push
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Push`). Unset, every referenced module runs and the API logs one
warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Push.PushModule())` in the `AddBarakoCMS` callback; discovery skips a
type the host already added. See `MODULES.md` in the repository.

## Configuration

```json
"Modules": {
  "Push": {
    "PublicKey": "base64url, 65 bytes",
    "PrivateKey": "base64url, 32 bytes",
    "Subject": "mailto:ops@example.org",
    "SendTimeoutSeconds": 10,
    "AllowedHosts": ["fcm.googleapis.com"]
  }
}
```

The module reads these under its own `Modules:Push` section, so the keys are `PublicKey`,
`PrivateKey` and `Subject` with no prefix. `Subject` is required whenever keys are set and must
start with `mailto:` or `https:`. Generate a key pair with `VapidKeyGenerator.Generate()`. Put the
private key in a secret store or an environment variable (`Modules__Push__PrivateKey`), never in
git.

With no keys configured, push is disabled: the module starts, `GET /api/push/config` reports
`enabled: false`, subscribe returns 503 and sends do nothing. A key that is set but malformed stops
startup. The message names the setting and never prints the value.

## Endpoints

| Method | Route | Who | Purpose |
| --- | --- | --- | --- |
| GET | `/api/push/config` | signed in | `{ enabled, publicKey }` |
| POST | `/api/push/subscriptions` | signed in | store or take over a device subscription |
| GET | `/api/push/subscriptions` | signed in | the caller's own subscriptions |
| DELETE | `/api/push/subscriptions/{id}` | signed in | remove one of the caller's own subscriptions; 404 for anyone else's |
| POST | `/api/push/test` | signed in | notify the caller's own devices |
| POST | `/api/push/send` | `send_push` | notify the users in `userIds`, or the whole tenant |

On `/api/push/send`, a null `userIds` broadcasts to the tenant, an empty list sends to no one, and
more than 500 ids is a 400. `send_push` is granted to the Admin role by default.

## Limits

- A user has at most 20 subscriptions. The 21st removes the least recently seen one.
- The payload cap is 3500 bytes. Larger messages are rejected (400 on `/send`).
- A subscription endpoint must be an https URL on port 443 whose host matches
  `Modules:Push:AllowedHosts`. The defaults are `fcm.googleapis.com`,
  `*.push.services.mozilla.com`, `*.notify.windows.com`, `web.push.apple.com` and
  `*.push.apple.com`. Setting `AllowedHosts` replaces the defaults.
- A send never follows redirects. A redirect comes back as its status code and counts as failed.
- Each send has a timeout, `SendTimeoutSeconds`, 10 by default.
- A 429 is not retried. It counts as failed.
- A 404 or 410 deletes the subscription.

## Workflow action

`Push` takes `Title` (cut to 80 characters), `Body` (cut to 140) and an optional `Url`, a path
starting with `/` (anything else becomes `/`). It notifies every subscriber in the workflow's
tenant. It fails closed: with an empty tenant it returns a permanent failure and sends nothing.

## Sending from code

Inject `IPushSender` and call `SendToUsersAsync(tenant, userIds, new PushNotification(title, body, url), ct)`
or `SendToTenantAsync`. The result counts attempted, delivered, pruned and failed sends. A payload
over 3500 bytes throws `ArgumentException` from `PushNotification.ToJson()`.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.

## License

[MIT](LICENSE) © BaryoDev
