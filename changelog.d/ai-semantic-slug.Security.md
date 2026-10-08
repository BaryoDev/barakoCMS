- **Semantic search returns a slug only when the type marks the slug field Public** (BarakoCMS.AI
  4.3.2). The module uses the same slug rule as delivery, and a hit's slug and title are read off
  the entry under the type's current fields at search time rather than taken from the index, so
  an entry indexed while its slug field was Public does not return it after the field is marked
  Sensitive.
