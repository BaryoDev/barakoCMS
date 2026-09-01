- **An unmapped content event no longer puts its class name in the history response.** The mapper
  fell back to `@event.GetType().Name` for an event it did not recognise, so adding an event and
  forgetting the switch would have published its CLR type name, which is the leak #229 forbids. No
  reflection guard can catch it, because by the time it reaches the wire it is a string. It reports
  `Unknown` now, the entry still appears so the count keeps matching the stream, and a behavioural
  test pins it.
