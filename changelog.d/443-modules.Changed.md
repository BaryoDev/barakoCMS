- **Every module endpoint asks for a capability instead of a role name.** Accounting, AI, Analytics,
  Diagnostics, Email, Feature flags, Files, Portability and PWA: 23 routes, twelve capability names.
  No endpoint in core or in a first-party module gates on a role name any more, which is what issue
  #443 set out to do.

  A module declares its own names, because core does not reference a module and a third-party one is
  not in this repository at all. Each module grants them at seed time to the roles its old gate
  listed, so turning `Auth:LegacyRoleFallback` off does not take a module away from the Admin role.
  Additive and idempotent, and a role the host never seeded is skipped rather than invented.

  Three gates that were one role list become two capabilities. Accounting separates reading the books
  from writing to them, so an auditor can read a ledger without posting to it. Analytics separates
  reading the numbers from creating a website in the upstream Umami account. Portability separates
  export from import, because reading a whole tenant out and writing a whole tenant in are opposite
  risks that one name could not tell apart.

  A `Accountant` role reached the whole accounting module by its name alone. It now reaches what it
  is granted, which after seeding is the same thing, and which an operator can now see and change.
