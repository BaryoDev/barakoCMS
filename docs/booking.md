# Booking

`BarakoCMS.Booking` lets a site sell time on a resource (a room, a stylist, a table) without double booking it. It ships in the Suite, or on its own as `dotnet add package BarakoCMS.Booking`.

## How it fits together

- A **resource** is a document: a name, a reference to any entry (`entryType`, `entryId`), an IANA `timeZone`, weekly `openingHours`, date `exceptions`, and `slotMinutes` (5 to 1440, or null for free-form).
- A **booking** is a content entry of the configured content type. You declare that type yourself, with string fields for the resource id, start, end and status.
- A **hook** runs inside the booking write. It refuses a booking that overlaps another active booking of the same resource. It takes a Postgres advisory lock per tenant and resource, so two concurrent requests for one slot give one booking.
- **Availability** is computed, never stored.

Intervals are half open: a booking that ends at 11:00 does not clash with one that starts at 11:00.

## Configuration

Section `Modules:Booking`:

| Key | Default | Meaning |
| --- | --- | --- |
| ContentType | `booking` | Content type holding bookings |
| ResourceField | `Resource` | Field with the resource id |
| StartField, EndField | `Start`, `End` | ISO 8601 with a UTC offset. End is exclusive |
| StatusField | `Status` | Field with the state |
| CancelledStatuses | `cancelled`, `canceled` | States that free the slot |
| MaxRangeDays | 31 | Longest availability range |
| MaxSlots | 2000 | Most slots in one response |
| PermitLimit, WindowSeconds | 60, 60 | Availability requests per IP per window |

## Routes

Admin, capability `manage_booking_resources` (granted to Admin by default):

- `GET /api/booking/resources` (paged)
- `GET /api/booking/resources/{id}`
- `POST /api/booking/resources` (201)
- `PUT /api/booking/resources/{id}`
- `DELETE /api/booking/resources/{id}` (409 while the resource holds bookings)

Public, anonymous, rate limited:

`GET /api/public/availability?resource=<id>&from=<instant>&to=<instant>`

`from` and `to` carry a UTC offset. The range must be positive and no longer than `MaxRangeDays`. The answer lists `{ start, end }` slots with offsets in the resource's zone, plus `truncated` when `MaxSlots` cut the list. It carries no booking data. An unknown or inactive resource is 404.

## Time zones

Hours are wall clock in the resource zone. On the day the clock springs forward the day is an hour short, and on the day it falls back it is an hour long. A wall time inside the gap moves forward by the gap; an ambiguous one takes the earlier instant.

## Refusals

A booking write is refused with 400 when the resource is missing, unknown or inactive, when start or end lack an offset, when end is not after start, or when the time is taken. The message for a taken time does not say who holds it.

## Not here yet

Holds with expiry (#580), a payment link (#838), .ics files and a staff feed. The hook does not check opening hours on write; availability does.
