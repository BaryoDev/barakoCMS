- **Delivery API: the routes under `/api/public` now have a written stability and deprecation
  policy, and no version segment.** #107 asked for URL versioning after 3.20.0 changed behaviour
  for every site in a minor release. The conclusion is that a second code path is the wrong cost for
  a project this size and would not have prevented 3.20.0 anyway. `docs/delivery-api.md` now says
  what counts as breaking, that a break lands only in a major, that it is announced under a Delivery
  API lead in this changelog at least one minor ahead, and that the old behaviour keeps working until
  then, a security fix being the one exception. D14 in `DECISIONS.md` records the alternative rejected and what would reopen it.
