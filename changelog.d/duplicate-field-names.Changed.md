- **A content type could declare two fields whose names differ only in case.** Creating a type, a
  blueprint and a portability import now refuse it, naming both spellings, since every
  reader finds a field by its name ignoring case. A type that already stores such a pair can still
  be changed.
