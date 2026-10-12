- **Journal entries were numbered from a single optimistic-concurrency row per year, so posts at once
  retried against each other.** BarakoCMS.Accounting 4.4.0 numbers them with the core gapless
  allocator, series `accounting.je`, in the same `JE-<year>-<six digits>` format.
  `migrations/5.0.0/accounting-number-sequences.sql`, shipped from the module, carries each
  tenant's yearly count over from the old rows, and its rollback copies the count back for a
  return to 4.9. `JournalEntryHook()` and `LedgerService(session, writer)` are obsolete and number
  through the same core counter, by the new `NumberAllocators.Gapless()`; the module registers the
  forms taking an `INumberAllocator`.
