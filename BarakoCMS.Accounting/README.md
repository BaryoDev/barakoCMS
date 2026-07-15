<div align="center">
  <img src="assets/logo.svg" width="96" height="96" alt="BarakoCMS.Accounting logo" />
  <h1>BarakoCMS.Accounting</h1>
  <p><em>An optional double-entry accounting module for barakoCMS.</em></p>
</div>

---

A drop-in **module** for [barakoCMS](https://github.com/BaryoDev/barakoCMS) that adds a general-purpose,
double-entry ledger: a chart of accounts, balanced journal entries, and reporting. It is
**chart-of-accounts agnostic** — you define the accounts; the module enforces the accounting.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Accounting.AccountingModule());
});

var app = builder.Build();
app.UseBarakoCMS();
await app.RunBarakoModuleSeedersAsync();   // seeds the "Accountant" role
```

The module wires itself in: it registers its services, its Marten document schema, its endpoints,
and a baseline `Accountant` role — no other setup.

## What it guarantees

- **Balanced by construction.** A journal entry is rejected unless total debits equal total credits, and every referenced account exists. The rule is enforced in the backend, not the UI.
- **Atomic.** An entry's lines are embedded in one document, so debit and credit commit in a single transaction — they can never diverge.
- **Immutable.** Posted entries aren't edited; corrections are reversing entries, preserving an audit trail.

## Endpoints

| Method & path | Purpose |
|---|---|
| `POST /api/accounting/journal-entries` | Post one balanced entry |
| `POST /api/accounting/accounts` | Create a chart-of-accounts entry |
| `GET  /api/accounting/accounts` | List the chart of accounts |
| `GET  /api/accounting/balances?asOf=` | Trial-balance-style balances |
| `GET  /api/accounting/accounts/{code}/ledger` | One account's ledger with running balance |

All are gated on the `Accountant` (or `Admin`/`SuperAdmin`) role.

## Model

`Account` (code, name, `AccountType`, optional `ParentCode` for sub-accounts) and `JournalEntry`
(embedded balanced `JournalLine`s) are strongly-typed Marten documents — money is real `decimal`,
and balances aggregate in the database. `LedgerService` posts; `ReportingService` reports.

Sub-accounts are just accounts with a `ParentCode`, which is how you model, say, a receivable
per customer or member under a single control account.

## Requires

barakoCMS ≥ 2.2.0 (for the module system). Targets .NET 8.

## License

[MPL-2.0](LICENSE) © BaryoDev
