- **Forms accept a choice field.** The Forms module predates the choice type, so a join form asking
  for an area of focus or a race sign-up asking for an entry type and a shirt size had nowhere to put
  the answer. `POST /api/public/forms/{slug}` now takes a `choice` field among the ones it accepts,
  validated the same way any write is: a value not offered is a 400 naming what is accepted, and a
  multiple choice field takes a list, even of one. `GET /api/public/forms/{slug}` lists each choice
  field's `options` (value and label, in order) and whether it is `multiple`, so a widget can draw a
  select, radios or checkboxes (BaryoDev/barakoPress#21). Additive, so `ApiContract.Version` does not
  move. (closes #845)
