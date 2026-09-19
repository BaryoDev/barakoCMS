- **A sensitivity change can be scheduled, the way publish and unpublish can.** `PUT
  /api/contents/{id}/schedule` takes `scheduledSensitivity` and `scheduledSensitivityAt` (both or
  neither; the time has to be in the future and the level different from the current one), the entry
  and its history report what is armed, and the scheduled sweep applies it as a real
  `ContentSensitivityChanged`, so delivery, masking, change webhooks and the history see it the way
  they see a manual change. The entry stays Published throughout: an unpublish time is "gone from the
  public site at a date", this is "still published, but only these roles may read it from that date".
  Resolution is the sweep interval, one minute. Additive on the request and the response, so
  `ApiContract.Version` does not move. (#824)
