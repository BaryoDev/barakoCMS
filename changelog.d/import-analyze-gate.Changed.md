- **`POST /api/import/analyze` asks for a capability.** It had no gate at all, so any authenticated
  caller could hand the server a spreadsheet to parse, and parsing is the expensive half. It now
  requires `analyze_spreadsheets`, which the module grants to Admin at seed time.

  One name covering the preview only. The bulk create next door is authorized on the target content
  type's own create permission, which is the right question for a write because it depends on what is
  being written. The preview has no target yet, since the mapping that names one is built from the
  preview it is about to return, so it asks the narrower question of whether you may use the import
  tool at all.
