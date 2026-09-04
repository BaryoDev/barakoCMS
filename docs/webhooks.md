# Webhooks: signing and the delivery log

A `Webhook` workflow action POSTs a JSON body to the URL it is configured with. With a secret set,
every delivery is signed so the receiver can tell a genuine delivery from anyone who learned the
URL. With or without one, every delivery leaves a row in the delivery log.

Retry is not here. Delivery is retried by the workflow runner today (up to five attempts with
backoff, see `docs/workflow-runs.md`), and a proper retry with dead-lettering arrives with the job
queue in #106.

## The secret

Add a `Secret` parameter to the action:

```json
{
  "type": "Webhook",
  "parameters": {
    "Url": "https://hooks.example.com/barako",
    "Secret": "whsec_a_long_random_value"
  }
}
```

It is encrypted with the deployment's `Secrets:Key` (falling back to `JWT:Key`) when the workflow
is saved. No read returns it: `GET /api/workflows` shows `secretSet: true` on the action and leaves
the parameter out. The runs, the execution log and the delivery log hold no copy either. Only the
action decrypts it, at the moment of sending.

If `Secrets:Key` is rotated, stored secrets can no longer be decrypted. The action then refuses to
send rather than sending unsigned, marks the attempt as a permanent failure with that reason, and
the fix is to enter the secret again on the workflow.

## Signed deliveries need https

A `Webhook` with a `Secret` must use an `https` URL. Over `http` the body and the signature travel
in cleartext, and whoever reads them can replay the delivery inside the receiver's tolerance window.
Creating or validating a workflow with a `Secret` and an `http://` URL fails with an error on
`actions[n].parameters.Url` that names this rule. A delivery that reaches the action anyway (a
definition saved before the rule) is recorded as a permanent failure with the same reason and
nothing is sent. Webhooks without a secret are unaffected.

```json
{
  "Webhooks": {
    "AllowInsecureSignedUrls": true
  }
}
```

`Webhooks:AllowInsecureSignedUrls` (default `false`) turns the check off at create and at delivery,
for a lab talking to a loopback receiver. `WebhookDeliveryTests` verifies the signing recipe over a
loopback `http` listener, which is the case the setting exists for.

## The headers

Every delivery carries:

| Header | Value |
| --- | --- |
| `X-Barako-Delivery` | The id of the delivery log row, so your log and ours can be joined. |
| `X-Barako-Timestamp` | Unix seconds when the request was signed. |
| `X-Barako-Signature` | `sha256=<hex>`, only when a secret is set. |
| `Idempotency-Key` | Stable across retries of the same action, so a retry can be recognised. |

The signature is HMAC-SHA256, keyed with the secret, over the string `"<timestamp>.<body>"`, where
`<body>` is the exact bytes of the request body. Including the timestamp is what lets a receiver
refuse a captured delivery replayed later.

## Verifying a delivery

Read the raw body before parsing it. A body re-serialised by your framework is a different string
and will not verify.

```python
import hmac, hashlib, time

def verify(secret: str, headers, raw_body: bytes, tolerance=300) -> bool:
    ts = headers["X-Barako-Timestamp"]
    if abs(time.time() - int(ts)) > tolerance:
        return False
    material = ts.encode() + b"." + raw_body
    expected = "sha256=" + hmac.new(secret.encode(), material, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, headers["X-Barako-Signature"])
```

`WebhookDeliveryTests` runs this recipe, written out in C#, against the bytes a real delivery put on
the wire and checks it fails with a different secret.

## The delivery log

`GET /api/webhook-deliveries` lists deliveries newest first, paginated, gated on `view_workflow_runs`
(the capability that reads workflow runs). Filters:

- `workflowId`: one workflow's deliveries.
- `status`: a class, one of `2xx`, `3xx`, `4xx`, `5xx`, or `failed` for a delivery that got no
  response at all (connection refused, timeout, a URL the outbound guard refused). An unknown value
  is a 400, not an empty list.

Each row holds the workflow id, the run id when the runner made the delivery, the URL with its
userinfo and query removed, the trigger event, the request headers minus the signature, the response
status, the first 4 KB of the response body, the duration, the error text when nothing answered, the
attempt number and when it happened.

The response body is kept because "what did they say" is the next question after "did it fire".
Some providers echo a credential in a 401 body, which is the reason the read is gated and the
reason the retention window is short.

## Retention

```json
{
  "Webhooks": {
    "DeliveryLogRetentionDays": 30
  }
}
```

Thirty days is the default. The sweep runs hourly, two minutes after start, on every tenant holding
deliveries. Zero or less keeps the log forever, the same reading `Workflows:Retention` uses, because
"0 days" reads as "delete immediately" just as naturally and keeping is the direction a mistake can
be recovered from.

This is an operational log, not an audit trail. The same caveat in `docs/workflow-runs.md` applies.
