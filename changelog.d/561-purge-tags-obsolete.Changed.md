- **`DeliveryCache.PurgeTags` is obsolete.** Nothing in barakoCMS calls it any more: purge on publish
  computes its tags with `DeliveryTagSet.ForCommitAsync`, the same set the response cache evicts. It
  still works and is removed in 6.0.0.
