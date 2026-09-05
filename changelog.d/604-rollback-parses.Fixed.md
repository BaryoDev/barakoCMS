- **`migrations/4.0.0/rollback-to-3.x.sql` parses.** The `DROP FUNCTION` for
  `mt_quick_append_events` carried `DEFAULT NULL::integer` over from the function's own definition,
  which `DROP FUNCTION` does not accept in its argument list. Applied with `--single-transaction`
  as the docs say, this meant nothing before the failing line landed either: the documented rollback
  did nothing at all. `scripts/upgrade-check.sh` now applies the rollback after the forward migration
  and boots 3.21.0 again against the result, so a future break here fails CI instead of an operator
  mid-incident.
