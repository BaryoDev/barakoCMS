- **CI runs the backend tests as four parallel shards.** The required check is still `Backend (.NET)`;
  it now waits for the shards and the script gates, and fails if any of them failed, was cancelled or
  was skipped. Classes are balanced by recorded durations (`scripts/test-class-durations.json`).
