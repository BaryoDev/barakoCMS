# BarakoCMS.Payments

Takes payment for an entry. Card and wallet details never reach barakoCMS: the visitor pays on the
provider's hosted page.

This is the first slice. It has the provider interface, checkout, the signed webhook and the
payment attempt record. It ships no provider adapter yet (PayMongo and Stripe are next), and
refunds and reconciliation are not built.

## Install

```sh
dotnet add package BarakoCMS.Payments
```

`BarakoCMS:Modules:Enabled` decides which modules run. Register a provider adapter in the
container as an `IPaymentProvider`.

## Declare a type payable

```
PUT /api/payments/payable/registration
Authorization: Bearer <token with manage_payments>

{ "enabled": true, "amountField": "fee", "currency": "PHP", "description": "Registration",
  "paidTransition": "MarkPaid", "failedTransition": "MarkFailed" }
```

The type needs a lifecycle with those transitions. Admin is granted `manage_payments` when the
module seeds.

## Checkout

```
POST /api/public/payments/checkout
{ "entryId": "...", "provider": "paymongo" }
```

Returns `{ "attemptId": "...", "checkoutUrl": "..." }`. The amount comes from the entry. An amount
in the request is ignored. The entry has to be in the state the paid transition starts from, or the
answer is 409.

## Webhook

`POST /api/payments/webhooks/{provider}`. The adapter verifies the signature over the raw body. A
bad signature is 401. Each provider event id is handled once, so a redelivery answers 200 and
changes nothing. A payment is confirmed by this webhook and by nothing else.

A provider cannot send `X-Tenant`, so in a multi-tenant host give each tenant its own webhook host
name.
