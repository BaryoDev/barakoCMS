- **A field sent under two spellings had only the first one checked.** Both were stored, so a reader
  taking the other spelling got a value nothing had checked. A write to a field of the type under
  more than one spelling, ignoring case, is now refused with a 400 on create, update, collection
  push, rollback and transition, and as a row error on bulk import, as file fields already were.
  This refuses some writes that used to be accepted.
