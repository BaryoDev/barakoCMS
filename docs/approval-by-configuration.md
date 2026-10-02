# Approval by configuration: an invoice, end to end against the API

A clerk raises an invoice and submits it. A manager, who may not edit the amount, approves it. The
clerk cannot approve, not even their own, and the manager cannot change what they are approving.
When the approval lands, the supplier gets an email. None of that is code: it is a content type
with a lifecycle, two roles with a transition permission each, one workflow on the `Approve`
transition, and a sender address in settings. This page walks that scenario against a running
instance, one curl per step, with the status code each one answers.

Every request below was run against the test host in this order and answered the code shown. The
pieces it uses are on master and tested: a lifecycle per type (`Models/ContentTypeDefinition.cs`,
`Features/Content/ChangeStatus/Endpoint.cs`, `ContentLifecycleTests`), permission on a transition
(`Infrastructure/Services/PermissionResolver.cs`, `TransitionPermissionTests`), a workflow that
fires on a transition (`Features/Workflows/WorkflowProjection.cs`) and email from settings
(`Features/Settings/Email/Endpoints.cs`, [configuring email](configuring-email.md)).

## Before you start

Bring up the [quickstart](../quickstart/) and export the base URL. The seeded administrator is
`ADMIN_USERNAME` / `ADMIN_PASSWORD` from your `.env`.

The quickstart pulls `ghcr.io/baryodev/barako-cms:latest`, which has lifecycles (they shipped in
4.0.0).

```bash
BASE=http://localhost:5005
```

Two settings are not in the quickstart compose and matter here. Add them under `environment:` of
the `api` service when the step says so, then `docker compose up -d api`.

| Key | Why |
| --- | --- |
| `Auth__RequireEmailVerification: "false"` and `Auth__AcknowledgeUnverifiedRegistration: "true"` | Step 4 registers two users. Registration emails a confirmation link. With no `RESEND_API_KEY` the suite image's send fails and is logged, and the core-only image's mock logs the recipient and subject only, so either way the link never reaches anyone. The first key turns verification off and the API refuses to start unless the second one says you meant it (`EmailVerificationOptions.Validate`). Remove both once the users exist. |
| `Lifecycle__AllowSelfTransition__Submit: "true"` | Step 6. Whoever raised an entry may not move it on, by default. This lets the clerk submit their own invoice. |

The examples use `jq` to pull ids out of responses. Read them off the JSON by eye if you do not have
it.

## 1. Sign in as the administrator

```bash
ADMIN=$(curl -s -X POST $BASE/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"YOUR_ADMIN_PASSWORD"}' | jq -r .token)
```

Answers `200` with `token`, `expiry`, `refreshToken`. The seeded admin holds `SuperAdmin` and
`Admin`, which is what the declaration steps below need.

## 2. Declare the invoice type with its lifecycle

```bash
curl -s -X POST $BASE/api/content-types -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{
  "name": "invoice",
  "displayName": "Invoice",
  "fields": [
    { "name": "Number",        "displayName": "Number",         "type": "string",  "isRequired": true },
    { "name": "Amount",        "displayName": "Amount",         "type": "decimal", "isRequired": true },
    { "name": "SupplierEmail", "displayName": "Supplier email", "type": "email",   "isRequired": true }
  ],
  "lifecycle": {
    "states": ["Draft", "Submitted", "Approved"],
    "initialState": "Draft",
    "transitions": [
      { "name": "Submit",  "from": "Draft",     "to": "Submitted" },
      { "name": "Approve", "from": "Submitted", "to": "Approved" }
    ]
  }
}'
```

`200` with `{"id": ..., "name": "invoice", "eventSourced": false}`.

The transitions are named, and the names are the whole point. A permission attaches to `Approve`
and a workflow fires on `Approve`. "Set the state to Approved" cannot be governed; "Approve" can.
An initial state not in `states`, a transition from or to a state not in `states`, or a
transition to its own state, is refused at declaration.

`ContentStatus` (Draft, Published, Archived) is untouched by any of this. It still decides whether
public delivery serves an entry. An approved invoice is not a published one.

## 3. Two roles: one raises, one approves

The clerk may create, read and update an invoice and perform `Submit`. The approver may read and
perform `Approve`, and nothing else.

```bash
CLERK_ROLE=$(curl -s -X POST $BASE/api/roles -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{
  "name": "InvoiceClerk",
  "description": "Raises invoices",
  "permissions": [{
    "contentTypeSlug": "invoice",
    "create": { "enabled": true },
    "read":   { "enabled": true },
    "update": { "enabled": true },
    "transitions": { "Submit": { "enabled": true } }
  }]
}' | jq -r .id)

APPROVER_ROLE=$(curl -s -X POST $BASE/api/roles -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{
  "name": "InvoiceApprover",
  "description": "Approves invoices",
  "permissions": [{
    "contentTypeSlug": "invoice",
    "read": { "enabled": true },
    "transitions": { "Approve": { "enabled": true } }
  }]
}' | jq -r .id)
```

Both answer `200` with `{"id": ..., "message": "Role created successfully", "unknownCapabilities": []}`.

`transitions` is separate from `update`, and not implied by it. A transition the role does not name
is refused, rather than falling back to the update rule. That fallback would be the obvious way to
keep old roles working and it would grant approval to everyone who can edit, which is the defect
this exists to remove. Case does not matter: a rule stored as `approve` matches a transition
declared `Approve`.

## 4. Two users in those roles

Add the two `Auth__` keys from the table above and restart the API, then register both accounts.

```bash
PASS='Walkthrough-Pass-2026!'
for u in clerk approver; do
  curl -s -X POST $BASE/api/auth/register -H 'Content-Type: application/json' \
    -d "{\"username\":\"$u\",\"email\":\"$u@example.com\",\"password\":\"$PASS\"}"
done
```

Each answers `200` with `"If that email address can be registered, we have sent it a link to
confirm it."` whatever happened, so the endpoint does not say which addresses exist. With
verification off the account is created on the spot. With it on (the default) the confirmation
link arrives by email and `POST /api/auth/register/verify` with `{"token": "..."}` completes it;
that is the path to use once a real provider is configured.

The password policy is twelve characters with upper, lower, a digit and a symbol. Registration is
rate limited to five per hour per client.

Sign both in, find their ids, and assign the roles. Listing users takes `SuperAdmin`.

```bash
CLERK=$(curl -s -X POST $BASE/api/auth/login -H 'Content-Type: application/json' \
  -d "{\"username\":\"clerk\",\"password\":\"$PASS\"}" | jq -r .token)
APPROVER=$(curl -s -X POST $BASE/api/auth/login -H 'Content-Type: application/json' \
  -d "{\"username\":\"approver\",\"password\":\"$PASS\"}" | jq -r .token)

USERS=$(curl -s "$BASE/api/users?page=1&pageSize=50" -H "Authorization: Bearer $ADMIN")
CLERK_ID=$(echo "$USERS" | jq -r '.items[] | select(.username=="clerk") | .id')
APPROVER_ID=$(echo "$USERS" | jq -r '.items[] | select(.username=="approver") | .id')

curl -s -X POST $BASE/api/users/$CLERK_ID/roles -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d "{\"roleId\":\"$CLERK_ROLE\"}"
curl -s -X POST $BASE/api/users/$APPROVER_ID/roles -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d "{\"roleId\":\"$APPROVER_ROLE\"}"
```

Logins answer `200` with a token. Both assignments answer `200` with
`{"message": "Role assigned to user successfully"}`. A user or role id that does not exist is a
`404`, not a success. The tokens issued before the assignment keep working: roles are read from the
stored user on every request, not from the token.

## 5. The clerk raises an invoice

```bash
INVOICE=$(curl -s -X POST $BASE/api/contents -H "Authorization: Bearer $CLERK" \
  -H 'Content-Type: application/json' -d '{
  "contentType": "invoice",
  "data": { "Number": "INV-1001", "Amount": 1250.00, "SupplierEmail": "supplier@example.com" }
}' | jq -r .id)
```

`200` with `{"id": ..., "version": 1}`. The entry starts in `Draft`, the lifecycle's initial state.

## 6. The clerk submits it, and is refused

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X PUT $BASE/api/contents/$INVOICE/status \
  -H "Authorization: Bearer $CLERK" -H 'Content-Type: application/json' \
  -d '{"transition": "Submit"}'
```

`403`. The clerk's role grants `Submit`, so this is not the permission. It is the self transition
rule: the person who raised a record may not move it on, and the check reads `CreatedBy`, so a
later edit by somebody else does not launder it. `SuperAdmin` does not bypass it either. The API
log says why:

```
Refused a self transition of <id> by its creator. Set Lifecycle:AllowSelfTransition:Submit to allow it.
```

Whether a raiser may submit their own work is a policy, and organisations answer it differently. It
is refused by default because that is the direction that can be relaxed later. Add
`Lifecycle__AllowSelfTransition__Submit: "true"` to the `api` service, restart, and run the same
request again:

```
200  {"message": "Submit moved this entry to Submitted"}
```

The switch is per transition. Allowing `Submit` says nothing about `Approve`, which stays refused
for the raiser even if the role granted it.

## 7. The wrong person, the wrong action, the wrong shape

The clerk tries to approve their own submission:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X PUT $BASE/api/contents/$INVOICE/status \
  -H "Authorization: Bearer $CLERK" -H 'Content-Type: application/json' \
  -d '{"transition": "Approve"}'
```

`403`. The role has `update` enabled and no `Approve` transition, and update does not stand in for
it. It is a `403` and not a `409`, deliberately: `409` would read as "not yet, come back once it is
submitted", and the clerk can never approve at any state.

The approver tries to change the amount:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X PUT $BASE/api/contents/$INVOICE \
  -H "Authorization: Bearer $APPROVER" -H 'Content-Type: application/json' \
  -d '{"data": {"Amount": 1.00}, "version": 2}'
```

`403`. A transition permission does not confer update, which is the other half of separation of
duties. The approver approves an amount they cannot touch.

Sending a status to a type that has a lifecycle:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X PUT $BASE/api/contents/$INVOICE/status \
  -H "Authorization: Bearer $APPROVER" -H 'Content-Type: application/json' \
  -d '{"newStatus": "Published"}'
```

`400`. A type with a lifecycle takes `transition`; a type without one takes `newStatus`, as every
type did before lifecycles existed. Sending both, neither, or the wrong one for the type is refused
rather than resolved by precedence.

## 8. A workflow on the approve transition

```bash
curl -s -X POST $BASE/api/workflows -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{
  "name": "Invoice approved: tell the supplier",
  "triggerContentType": "invoice",
  "triggerEvent": "transition:approve",
  "actions": [{
    "type": "Email",
    "parameters": {
      "To":      "{{data.SupplierEmail}}",
      "Subject": "Invoice {{data.Number}} approved",
      "Body":    "Invoice {{data.Number}} for {{data.Amount}} was approved."
    }
  }]
}'
```

`200`, and the response carries `"triggerEvent": "transition:Approve"`: the trigger is stored in
the casing the type declares, because the engine matches it with an equality query and a workflow
saved as `transition:approve` would otherwise never fire. The trigger is the transition name, not
the state it lands in. "State is now Approved" also describes an administrator correcting a
mistake, and a supplier notification is the thing that most needs not to fire on that.

`{{data.Field}}` reads the entry's fields; `{{id}}`, `{{contentType}}`, `{{status}}` and
`{{createdAt}}` are also available (`GET /api/workflows/variables` lists them). The
`Email` action needs `To`, `Subject` and `Body`. Values in `Body` are HTML-encoded, and values in
`Subject` and `To` lose their line breaks: see "Values in a workflow email" in
[configuring-email.md](configuring-email.md). Dates, amounts, the author and the transition are
under [Placeholders](#placeholders) below.

A workflow on a transition the type does not declare is refused:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST $BASE/api/workflows \
  -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d '{"name":"bad","triggerContentType":"invoice","triggerEvent":"transition:Pay",
       "actions":[{"type":"Email","parameters":{"To":"x@example.com","Subject":"s","Body":"b"}}]}'
```

`400`, naming the declared transitions. A workflow that saves and never fires looks exactly like
one that fires and fails, so the name is checked when it is saved.

One workflow can fire for several types. Send `"triggerContentTypes": ["invoice", "credit-note"]`
alongside or instead of `triggerContentType`; the workflow fires for `triggerContentType` and every
type in the list. The response carries both: the list with every type, and `triggerContentType`
holding the first. A blank entry is refused. With a transition trigger, every listed type has to
exist and declare that transition with the same spelling, and each one that does not is named in
the `400`.

Events work the same way. Send `"triggerEvents": ["Published", "Unpublished"]` alongside or instead
of `triggerEvent`; the response carries the list with every event and `triggerEvent` holding the
first. An event fires a workflow once, however many list entries it matches.

`Unpublished` fires when a Published entry changes to any other status. The status before the
change is read from the entry's event stream. An entry with no status on record there (seeded, or
stored without events) fires no `Unpublished` on its first status change; the API logs that at
Information with the entry's id and type. Later changes are on record and fire as usual.

`Deleted` fires when an entry is erased. Its run carries the entry's id and content type only:

- A `Deleted` workflow with conditions is saved but never fires, because its conditions read data
  that is gone.
- `{{id}}` and `{{contentType}}` resolve as usual. `{{status}}`, `{{createdAt}}` and
  `{{updatedAt}}` resolve to nothing, and a `{{data.X}}` token is left as written.
- A `Conditional` action whose condition reads `{{status}}` or `{{data.X}}` fails, and is not
  retried. A condition on `{{contentType}}` works.
- A `Webhook` body holds `event`, `tenant`, `contentId` and `contentType` and nothing else (see
  [webhooks.md](webhooks.md)).
- A custom `IWorkflowAction` is told by its `TriggerEvent` parameter, which is `Deleted`. The
  `Content` it is handed has the entry's `Id` and `ContentType`; every other member is a default
  and not a stored value, so the action should not send or store them.

## 9. The sender, from settings

```bash
curl -s $BASE/api/settings/email -H "Authorization: Bearer $ADMIN"
```

`200` with `apiKeySet`, `apiKeySource`, `fromAddress`, `fromAddressSource`, `updatedAt`,
`updatedBy` and `providerRegistered`. Each source is `Stored`, `Configuration` or `None`, so the
screen can say where a value came from. The key itself is never in the response.

The quickstart's default image is the suite, which registers the Resend provider, so
`providerRegistered` is true there even with `RESEND_API_KEY` blank; a send then fails with "No
Resend API key is set" and the workflow run records that. The core-only image (`barako-cms-decaf`)
has no provider and its mock logs each message's recipient and subject and delivers nothing. Set
`RESEND_API_KEY` and `RESEND_FROM` in `.env`, or store them here, which wins over the deployment's
value and takes effect on the next send with no restart:

```bash
curl -s -X PUT $BASE/api/settings/email -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{"fromAddress": "Accounts <accounts@example.com>"}'
```

`200` with `"fromAddressSource": "Stored"`. `apiKey` goes in the same request when you have one; it
is stored encrypted and never returned. This endpoint is `SuperAdmin` (the `ManageEmailSettings`
capability), not `Admin`, because redirecting where the system's mail comes from redirects every
password reset in the deployment. [Configuring email](configuring-email.md) has the precedence
rules and the test send.

## 10. The approver approves

```bash
curl -s -X PUT $BASE/api/contents/$INVOICE/status -H "Authorization: Bearer $APPROVER" \
  -H 'Content-Type: application/json' -d '{"transition": "Approve"}'
```

`200` with `{"message": "Approve moved this entry to Approved"}`.

The transition is appended to the entry's event stream, the workflow projection picks it up, and
the runner sends the email. On the test host the message recorded was:

```
to: supplier@example.com
subject: Invoice INV-1001 approved
body: Invoice INV-1001 for 1250 was approved.
```

On a quickstart with no key the run records the provider's refusal instead, and on the core-only
image the mock logs the recipient and subject. Either way the run is on record:

```bash
curl -s "$BASE/api/workflow-runs?contentId=$INVOICE" -H "Authorization: Bearer $ADMIN"
```

`200`, one run for the workflow, with an `Email` action, its status and its error if it had one.
The runner polls every five seconds, so allow that long. [Workflow runs](workflow-runs.md) covers retries and retention.

The entry's history shows the moves as `Transitioned` entries:

```bash
curl -s $BASE/api/contents/$INVOICE/history -H "Authorization: Bearer $ADMIN"
```

`200`, with `changeType` `Created`, `Transitioned`, `Transitioned`.

Approve it again:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X PUT $BASE/api/contents/$INVOICE/status \
  -H "Authorization: Bearer $APPROVER" -H 'Content-Type: application/json' \
  -d '{"transition": "Approve"}'
```

`409`: `'Approve' moves Submitted to Approved, and this entry is Approved.` The approver is told
the state because they are entitled to act on it. A caller with no rights on the type gets a bare
`403` from the same request and is told neither the entry's state nor the type's transitions. A
second run does not fire, and the supplier is not emailed twice.

`Lifecycle:EnforceTransitions` (default on) is what makes that a `409`. Off, the move is allowed
and logged at warning level, which exists for a deployment whose entries predate its rules.

## What each answer meant

| Request | Caller | Answer | Why |
| --- | --- | --- | --- |
| `Submit` on own invoice | clerk | `403` | Self transition, refused by default |
| `Submit` on own invoice, `Lifecycle:AllowSelfTransition:Submit` on | clerk | `200` | Policy relaxed for that one transition |
| `Approve` | clerk | `403` | Role grants update and Submit, not Approve; update does not imply it |
| `PUT /api/contents/{id}` | approver | `403` | Role grants Approve, not update |
| `{"newStatus": ...}` on a lifecycle type | approver | `400` | Wrong shape for the type |
| Workflow on `transition:Pay` | admin | `400` | Not a declared transition |
| `Approve` from Submitted | approver | `200` | Granted, in order, not the raiser |
| `Approve` from Approved | approver | `409` | Out of order, and the caller may know that |

## A transition that requires fields

This part is not in the walkthrough above and its requests were not run against the quickstart. It
is tested by `TransitionRequiredFieldsTests`.

A transition can name fields of the type that must be sent with the move, and fields that may be:

```json
{ "name": "Reject", "from": "Submitted", "to": "Rejected",
  "requiredFields": ["RejectionReason"], "optionalFields": ["RejectionNote"] }
```

Both lists are optional and empty by default. Each name has to be a field the type declares, and a
name may appear once across the two lists; anything else is refused when the type is saved.

The values go in `data` on the same request that makes the move:

```bash
curl -s -X PUT $BASE/api/contents/$INVOICE/status -H "Authorization: Bearer $APPROVER" \
  -H 'Content-Type: application/json' \
  -d '{"transition": "Reject", "data": {"RejectionReason": "No receipt attached"}}'
```

- Without a value for `RejectionReason` in `data` the answer is `400` naming the field, and the
  entry stays where it was. A blank string and `null` count as no value. A value already on the
  entry does not count either: an entry rejected, sent back and rejected again needs a reason of
  its own, or the workflow would send the first one out a second time.
- `data` may carry only the fields the transition declares. Any other key is a `400`, and the
  answer lists the fields the transition takes without repeating the key that was sent.
- The caller needs the transition permission and not `update`. That is how a reviewer who may not
  edit the entry still records why they rejected it, and the declared lists are the limit of what
  they can write.
- The values are checked the way an update checks them: a field the caller may not see is put back
  to its stored value, the type's validation runs over the whole entry (required fields, types and
  `validationRules`), and before-save hooks run. A reviewer who may not see a required field cannot
  send it, so the move is refused with the field named, whatever the entry already holds.
- The permission checks run first. A caller who may not perform the transition gets the same bare
  `403` as before and is not told which fields it requires.
- The values and the move commit together. The entry's history shows an `Updated` beside the
  `Transitioned`, and a workflow on `transition:Reject` reads `{{data.RejectionReason}}`. A workflow
  on `Updated` fires too, because the data did change.
- When `data` is sent, an edit to the entry by somebody else that commits while the move is in
  flight makes the move a `409`, and an edit that committed just before is kept. The move does not
  write another writer's field back to what it was.
- `data` sent to a transition that declares no fields, or with `newStatus`, is ignored.

The requirement is checked where a transition is made, which is this endpoint and nothing else.
Scheduled publishing, `UpdateField`, collection pushes and syncs change `status` and never the
lifecycle state. A workflow can still blank the field afterwards: this is a check on the move, not
a rule the entry keeps.

An import replaces a type's fields and keeps its lifecycle, so it refuses a bundle that leaves out
a field a stored transition names, one that changes what a stored transition requires, and one
whose new lifecycle names a field the bundle's type does not declare. A stored transition that
names a field the type does not have anyway is skipped for that name and logged at warning level,
and the rest of what it requires still applies.

## Placeholders

A `{{...}}` in a workflow action parameter holds one of the shapes below. Anything else between
the braces is sent as written.

| Written as | Gives |
| :--- | :--- |
| `{{createdAt}}`, `{{data.Field}}` | The value as stored |
| `{{createdAt \| date "MMM d, h:mm tt"}}` | A date in the site's time zone, in a .NET date format of at most 64 characters. With no format it is `yyyy-MM-dd HH:mm` |
| `{{createdAt \| date "h:mm tt" "Asia/Manila"}}` | The same in the zone named, whatever the site says |
| `{{data.Amount \| money}}` | Two decimals with thousands separators, after the site's currency code when it has one: `PHP 1,250.00` |
| `{{data.Amount \| money "USD"}}` | The same after the code named |
| `{{data.Name \| upper}}`, `{{data.Name \| lower}}` | The value in capitals or in small letters |
| `{{duration createdAt transition.at}}` | The time between two dates: `8 hours 30 minutes`. Seconds are dropped |
| `{{hours createdAt transition.at}}` | The same in hours to one decimal: `8.5` |
| `{{createdBy.name}}`, `{{createdBy.email}}` | The username and address of whoever created the entry |
| `{{transition.name}}`, `{{transition.at}}` | The transition that fired the workflow, and when |
| `{{transition.by.name}}`, `{{transition.by.email}}` | Whoever made that transition |

The time zone is the `TimeZone` field of the tenant's published `site` entry
([site-settings.md](site-settings.md)) and the currency is its `Currency`. With no `site` entry or
no `TimeZone`, dates are UTC. Month and day names, `AM` and `PM`, and the number separators are the
invariant (English) ones on any server.

A date is `createdAt`, `updatedAt`, `transition.at`, or a field holding an ISO 8601 date
(`2026-09-14`, `2026-09-14T08:30Z`, `2026-09-14T08:30:00+08:00`). A time with no zone on it is read
as UTC. An end before its start gives a duration with a minus sign in front.

The `transition.` placeholders are filled only when the trigger is a transition. Where no user is
behind a change (a scheduled publish, an import, an account since deleted), the name and the
address are empty. In a registered tenant they are also empty for a user who is not an active
member of it: a platform administrator working in the tenant, a member since removed or suspended,
the owner of an API key who has no membership there. The default tenant and a tenant nobody
registered have no memberships, and every user of the deployment is named there.

What the engine cannot fill is sent as written: an unknown name or format, a `date` on a value that
is not a date, a `money` on one that is not a number, a time zone the server does not know (the
site's included), a format .NET refuses. Saving a workflow lists what can be told from the template
alone in `warnings`, each with the parameter it is in, on the `POST /api/workflows` response and
on `POST /api/workflows/validate`. A warning never refuses the save. A `data.` field is not
checked, since whether an entry has it is only known when one arrives. A warning quotes the
placeholder it is about, except in a credential parameter (the ones the responses leave out), where
it gives a count. Other workflow responses (the list, switching one on or off) do not check and
carry no `warnings` field, so an empty array always means checked and clean.

An `UpdateField` or `CreateTask` parameter that names `{{createdBy.email}}` or
`{{transition.by.email}}` gets a warning of its own: those actions write into an entry, and a
public field of a deliverable type is served by delivery, address included. The save is not
refused, because the field and the entry such an action writes can themselves be placeholders.

A formatted value, a name and an address are encoded like any other value: HTML-encoded in an
`Email` body, stripped of line breaks in its `Subject` and `To`.

`POST /api/workflows/dry-run` does not look a user up. It fills the author and the transition with
`sample.user` and `sample.user@example.com`, and takes the sample entry's `updatedAt` as the
transition time.

A request definition sent through a connector has its own placeholders
([connectors.md](connectors.md)) and does not read these formats. Neither does the condition of a
`Conditional`.

## What this page does not cover

`GET /api/contents/{id}` does not return the lifecycle state today; the status change response,
the history and the workflow runs are where it shows. `Submitted` and `Approved` do not change
`status`, so an approved invoice stays out of public delivery unless somebody also publishes it
and the type is opted in (`isPubliclyDeliverable`). Conditions on a transition permission (row
level rules, [access control](access-control.md) layer 2) apply to transitions the same way they
apply to update, and are not exercised here.
