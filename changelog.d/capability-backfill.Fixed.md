- **A capability added after a deployment upgraded now reaches its seeded roles.** The backfill
  filled only an empty capability list, so a deployment that upgraded once had an Admin whose list
  was not empty, and every area migrated afterwards never arrived. Nothing broke while
  `Auth:LegacyRoleFallback` was on, since the gate still honours the role names it replaced. Turning
  the fallback off, which is the point of the migration, is where that Admin would have silently lost
  every area migrated after its own upgrade.

  The defaults are unioned in on each seed instead. The cost, stated rather than hidden: a default an
  operator has deliberately removed from a seeded system role comes back on the next restart, because
  nothing records that the removal was deliberate. Removing one for good means not running the
  seeder. A role you created is untouched either way, since the defaults are keyed on the names the
  seeder creates.
