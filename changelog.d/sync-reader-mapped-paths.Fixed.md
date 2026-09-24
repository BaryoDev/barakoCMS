- **A collection sync could drop a mapped field from a large JSON item.** The reader kept the first
  200 paths of every item, and a real GitHub issue is about 200 paths, so a mapping of
  `state_reason` came back empty once an issue had one more label. It now keeps only the paths the
  mapping, rules and exclusions name.
- **`CollectionSyncs:Enabled=false` did not stop the sweep in a host that adds configuration late.**
  The switch was read only when services were registered. The sweep now checks it again when it
  starts.
