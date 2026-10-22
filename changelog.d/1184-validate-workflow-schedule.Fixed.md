- **Validating a schedule-triggered workflow reads its schedule fields.** `schedule`, `scheduleTimeZone`
  and `scheduleCatchUp` are now accepted by the validate endpoint, so a bad cron expression or time
  zone is reported there instead of passing.
