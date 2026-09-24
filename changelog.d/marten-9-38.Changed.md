- **Marten moves from 9.37.0 to 9.38.0, which stops a failed batch leaving a permanent gap in the
  event sequence.** Under the default append mode a `StartStream` drew sequence numbers nothing read
  back, so when anything later in the same batch failed, the gap it left stalled the projection
  daemon for every tenant until the stale-sequence threshold passed. 9.38 sends stream creation
  through `mt_quick_append_events`, and replaces that function's body so a new stream's row is
  written once instead of twice. The app runs `AutoCreate.CreateOnly`, which never replaces an
  existing function, so an upgraded database needs
  `migrations/4.4.0/marten-9-38-quick-append-events.sql` applied while the old build is still
  serving. It touches no data. Rolling back needs the matching file in the same directory. JasperFx
  moves to 2.73.2 and Weasel to 9.32.0 with it.
