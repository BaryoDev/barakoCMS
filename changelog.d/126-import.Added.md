- **A screen for importing a spreadsheet.** The Import module had two endpoints and no interface, so
  turning an .xlsx or CSV into entries meant calling the API by hand. Settings now has one:
  choose a file, say which row holds the headings, match each column to a field on the target content
  type, and import.

  Entries are created as drafts, so nothing an import gets wrong is published. Every row is attempted
  and the refusals are reported by their position in the sheet, rather than the first bad row ending
  the run and leaving an editor to work out how much of it landed. A column matched to nothing is
  left out rather than sent blank: a sheet usually carries a column nobody wants, and sending it
  would either fail validation or invent a field the type never declared.
