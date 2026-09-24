- **A collection sync mapping a datetime field rewrote every entry on every run.** The stored value
  and the fetched one were compared as text, and the two were never spelled the same, so each run
  appended a `ContentUpdated` to every entry and fired its workflows. A datetime is now compared as
  an instant, and a second run against an unchanged source reports every entry unchanged.
