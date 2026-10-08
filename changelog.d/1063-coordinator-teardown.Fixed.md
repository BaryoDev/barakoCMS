- **Test teardown stops each projection coordinator first.** The integration fixture stops the
  coordinator of every host it started before any host stops, so the host's own stop finds it
  already stopped. An `ObjectDisposedException` from that stop is written to stderr instead of
  failing every test in the collection; anything else still fails it.
