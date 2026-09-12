- **`Sensitivity:Mode=All` is now refused at startup instead of running inert.** It was declared but
  never implemented: scrubbing branches on `Off` and nothing else, so `All` behaved exactly as
  `SensitiveOnly` while accepting the setting and starting cleanly. An operator who sets it has
  decided they need strict lockdown, which is the one case where getting `SensitiveOnly` silently is
  worst. Refused the same way `Erasure:Mode=CryptoShred` is, and for the same reason. Use
  `SensitiveOnly`, which is the default.
