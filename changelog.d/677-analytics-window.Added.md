- **Analytics could only show four fixed windows ending now.** The Umami routes (`summary`,
  `series`, `metric`) take `from` and `to` for any window up to 400 days, the series answers with
  the window it covered, and `compare=previous` adds the window of the same length just before it.
  `BarakoCMS.Analytics.Umami` is 4.4.0.
