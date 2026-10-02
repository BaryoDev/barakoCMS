- **Adding a field to an event-sourced content type now checks the field's sensitivity.**
  `POST /api/content-types/{name}/fields` answers 400 for a Sensitive or Hidden field on an
  event-sourced type, the rule type creation, `PUT .../fields/{field}/sensitivity` and the
  Portability import already apply. A Public field is added as before, and so is any field on a
  type that is not event sourced. `POST /api/content-types` now compares the name with stored
  types and entries the way the sourcing decision is keyed (trimmed, lowered, a space as a
  hyphen), so a name that differs from a stored type's only by those answers 409, and a name
  with entries under such a spelling cannot be created as event sourced. Stored types are not
  changed. The demo seed records the `AttendanceRecord` name as document sourced with the type,
  and is skipped when that name was decided as event sourced, since the demo type holds a
  Sensitive field. These refusals are part of contract 6, so `ApiContract.Version` does not move
  again.
