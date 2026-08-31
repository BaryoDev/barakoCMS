# Trusted-device binding + OTP device context (design)

> Status: built and shipping as `BarakoCMS.DeviceTrust`. This is the original
> design note, kept for the reasoning behind the shape. For how to turn it on and
> what each setting does, read the module's own README
> ([BarakoCMS.DeviceTrust/README.md](../BarakoCMS.DeviceTrust/README.md)), which
> describes the code as built. Where the two disagree, the README is right.
>
> Driven by BaryoClub (block transactions from unapproved devices), but the
> capability is generic, so it belongs in barakoCMS, not in the club app.

## What we want

1. The sign-in OTP email names the requesting device, Maya-style:
   *"You are trying to sign in using Chrome on macOS from 120.28.x.x (Manila).
   Sharing this code lets another device or person access your account. DO NOT SHARE."*
2. Track each user's approved devices.
3. Bind a session to the device it was approved on, and know device + location
   per sensitive action (transaction).
4. Block API calls that come from a device the user has not approved.

## Where it lives

**Recommendation: a new opt-in module, `BarakoCMS.DeviceTrust`** (same shape as
`BarakoCMS.Accounting` / `Files` / `Email.Resend`), plus four small **generic
seams in core**. Not hardcoded in core (not every barakoCMS app wants device
binding), not in `ClubManager.Api` (it is universal security, reusable by any
app, shippable as a NuGet).

The module owns device records, trust lifecycle, enforcement, and device-
management endpoints. Core only gains the minimum hooks the module needs.

### The four core seams (the only changes to barakoCMS core)

1. **OTP request captures device context.** Read `User-Agent`, client IP, and an
   `X-Device-Id` header in `RequestEndpoint`; include a device-description line
   in the email. Also fix the hardcoded "BaryoClub" in the OTP body to a
   configurable app name (`Branding:AppName`) while we are in there. Generically
   useful on its own.
2. **A hook on OTP-verify success.** An `IOtpVerifiedHandler` (default no-op)
   invoked in `VerifyEndpoint` with the user, the device context, and the
   claims builder, so the module can upsert/approve the device and add a device
   claim to the JWT.
3. **`RefreshToken` gains an optional `DeviceId`.** So a refreshed session stays
   bound to the same device and can be revoked per device.
4. **A module pipeline seam.** `IBarakoModule` today has no way to add
   middleware or a FastEndpoints global processor. Add a contribution point
   (e.g. modules can register `IGlobalPreProcessor`s, collected in
   `UseFastEndpoints`). The module registers the enforcement pre-processor here.
   This is a generally useful seam other modules will want too.

Everything else is inside `BarakoCMS.DeviceTrust`.

## Device identity

The client generates a random UUID `deviceId` on first run, persists it
(localStorage on web, secure storage in the MAUI app), and sends it as
`X-Device-Id` on every request. The server parses `User-Agent` for a friendly
description and resolves the IP to a rough location.

A client-supplied id is not hardware attestation, so we do not trust it alone,
we bind it (below). Stronger proof (device keypair / passkeys) is a later
hardening step.

## Data model (module Marten documents)

```
Device
  Id, UserId, DeviceId(hash of the client token), Label,
  UserAgent, Browser, Os, FirstSeenIp, LastSeenIp, LastSeenGeo,
  Status: Pending | Trusted | Revoked,
  CreatedAt, TrustedAt, LastUsedAt

DeviceActivity            // "where and on what device" per transaction
  Id, UserId, DeviceId, Ip, Geo, Action, Path, At
```

Plus the core `RefreshToken.DeviceId` from seam #3.

## Flows

1. **Request OTP**: capture UA/IP/deviceId, upsert a `Pending` device, and put
   the device line in the email.
2. **Verify OTP**: on success (seam #2), mark the device `Trusted` for that
   user, issue the JWT with a `did` claim = the approved deviceId, store the
   refresh token with `DeviceId`. OTP *is* the approval step, so a brand-new
   device becomes trusted precisely by passing an email code.
3. **Enforcement** (module global pre-processor, seam #4), for authenticated
   requests: read `did` from the JWT, require `X-Device-Id` to match it, and
   confirm the device record is `Trusted`. Otherwise reject (401), which tells
   the client to run OTP to approve this device.
4. **Audit**: a post-processor writes `DeviceActivity` on sensitive endpoints,
   giving the per-transaction "who/where/what device" trail.

## What counts as "must be an approved device"

### What shipped

One boolean, `DeviceTrust:Enforce` (env `DeviceTrust__Enforce`).

Off, which is the default, records devices and blocks nothing. On, a request
whose token carries a `did` claim must send a matching `X-Device-Id`.

Read the second half of that carefully, because it is the part an operator will
get wrong. Enforcement applies to **device-bound tokens only**. A token with no
`did` claim passes through untouched, by design: anonymous endpoints and tokens
issued before the feature was switched on would otherwise all start failing.
So turning `Enforce` on does not mean every authenticated endpoint now requires
an approved device. It means a token that was bound to a device cannot be
replayed from a different one.

### The three modes below are design, not code

Nothing implements `Off`, `SensitiveOnly` or `All`, and there is no
`DeviceTrust:Enforcement` setting. Kept because the shape is still the intended
direction, not because you can configure it:

- **Off**: observe only (record devices, no blocking). Good first rollout.
- **SensitiveOnly**: enforce on endpoints marked sensitive. BaryoClub tags
  payment and journal endpoints sensitive, so transactions are device-gated
  while reads are not.
- **All**: every authenticated endpoint requires a trusted device.

## Why binding beats a bare check

The `did` claim ties the token to its device, so a stolen JWT is useless from
another device (its `X-Device-Id` will not match the claim). The `Device.Status`
lookup gives server-side revocation, killing a session by revoking its device.
Refresh is device-bound too (seam #3), so a leaked refresh token cannot mint
sessions elsewhere.

## Geolocation

An `IGeoLocator` with a no-op default; a MaxMind GeoLite2 local DB (or a
provider) fills in city/country for the email and audit. IP shows even without
it. This is PII, so note retention limits.

## Endpoints (module)

```
GET  /api/devices                 my devices (RBAC row-level: own only)
POST /api/devices/{id}/revoke     revoke a device (ends its sessions)
GET  /api/admin/devices           admin view (optional, role-gated)
```

## Frontend (BaryoClub web + future MAUI)

- Generate/persist `deviceId`; send `X-Device-Id` on every API call.
- "Your devices" screen: list, current device highlighted, revoke others.
- On a 401-from-untrusted-device, prompt the OTP flow to approve this device.

## Honest caveats

- Do not lock the user out: first-device bootstrap, OTP is always a recovery
  path to approve a new device, and an admin override exists.
- `X-Device-Id` is client-controlled; binding raises the bar but is not
  attestation. Passkeys / WebAuthn or a device keypair (DPoP-style) are the real
  hardening and can layer on later.
- Storing IP/UA/geo is PII, keep a retention window.

## Phasing

- **Phase 1**: seam #1 (OTP device context + configurable app name) + device
  records + bind-on-verify. Enforcement `Off` (observe, build the device list).
- **Phase 2**: seams #2, #3, #4: `did` claim, refresh binding, enforcement
  pre-processor at `SensitiveOnly`, "Your devices" UI.
- **Phase 3**: geolocation, admin device console, per-transaction activity
  audit, hardening (passkeys).

Phase 1 is safe to ship first: it improves the OTP email and starts recording
devices without the risk of blocking anyone.
