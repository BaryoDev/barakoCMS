- **Cash flow no longer counts a transfer between cash accounts as a receipt and a disbursement.**
  `ReportingService.CashFlowAsync` now nets each entry's cash lines and classifies that net, so a
  deposit from cash on hand to the bank adds to neither side. Detail lines still show both legs and
  closing is unchanged. Accounting 4.5.0.
