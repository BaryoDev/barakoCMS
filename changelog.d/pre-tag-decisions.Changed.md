- **Three decisions recorded before the 4.0 tag, in `DECISIONS.md`.** D16 extends expected-version
  concurrency to document types, because moving from last-write-wins to a 409 is a breaking change
  and 4.0 is the last moment it costs nothing; `Content:Concurrency:Require` keeps the 3.x upgrade
  path working and flips in 5.0. D17 settles that a money value stays a plain number, with currency,
  scale and rounding declared on the field definition, so the stored shape and the delivery contract
  do not change. D18 states what module authors are promised: a replacement for `ConfigureMarten`
  before 5.0 removes it, a default implementation and a deprecation window for every added member,
  and `IWorkflowAction` documented as the extension point it already is.
