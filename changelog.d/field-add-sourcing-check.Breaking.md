- **Adding a field to an event-sourced content type now checks the field's sensitivity.**
  `POST /api/content-types/{name}/fields` answers 400 for a Sensitive or Hidden field on an
  event-sourced type, the rule type creation, `PUT .../fields/{field}/sensitivity` and the
  Portability import already apply. A Public field is added as before, and so is any field on a
  type that is not event sourced. Stored types are not changed. The demo seed is skipped when the
  `AttendanceRecord` name was decided as event sourced, since the demo type holds a Sensitive
  field. This refusal is part of contract 6, so `ApiContract.Version` does not move again.
