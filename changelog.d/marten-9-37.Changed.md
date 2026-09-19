- **Marten moves from 9.30.0 to 9.37.0, and the event store gains the columns that make the
  projection daemon observable.** `mt_event_progression` now records, per shard, which node holds it
  and its heartbeat, whether it is paused and why, how far behind it is against a warning and a
  critical threshold, and the sequence, type and tenant of the event that failed it. Until now a
  projection that died took every workflow with it and said nothing beyond a health check we wrote
  ourselves, which is the failure `WorkflowProjection` documents against itself. `mt_streams` gains
  the compaction watermark Marten 9.32.0 added. Both are ALTER statements on tables that already
  exist, and the app runs `AutoCreate.CreateOnly`, so an upgraded database needs
  `migrations/4.3.0/marten-9-37-event-store-columns.sql` applied while the old build is still
  serving. On PostgreSQL 11 and later each statement is a catalogue change rather than a table
  rewrite, so it is fast on an event store of any size. Rolling back needs the matching file in the
  same directory, because a pre-4.3.0 build refuses to start against a database carrying the new
  columns. JasperFx moves to 2.72.0 with it.
