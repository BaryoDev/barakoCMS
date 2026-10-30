# migrations/removed

The migration scripts of the modules removed in 5.5.0 (#1284), and of the stock reservation
primitive. Core does not embed or run anything in this folder, and `scripts/lib-migrations.sh` does
not plan it. The files are here so an operator who ran those modules can still drop what they made.

Nothing in 5.5.0 touches these tables. A database that has them starts without errors, and
`db-migrate --status` prints their old ledger rows as `not-loaded`.

## What is in here

- `<version>/<name>.sql` and `<version>/rollback-<name>.sql`: the forward and rollback files exactly as
  they shipped, moved here unchanged. `ShippedMigrationTests` pins each by checksum.
- `drop/<module>.sql`: one drop script per module that made tables. Run one when you are sure you want
  the data gone. Each drops the module's tables, is safe to run twice, and for the modules that
  recorded ledger rows also deletes those rows from `public.barako_migrations`.

```
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/drop/forms.sql
```

Take a backup first. A dropped table cannot be brought back.

If you replace a removed module with another package that keeps using its tables, do not run that module's drop script. It deletes the data the replacement reads.

## Tables and ledger owners

| Module | Tables | Ledger owner |
| --- | --- | --- |
| Payments | `mt_doc_payments_attempts`, `mt_doc_payments_events`, `mt_doc_payments_payable_types` | Payments |
| Booking | `mt_doc_booking_resources` | Booking |
| Scheduling | `mt_doc_scheduled_entries` | Scheduling |
| Services | `mt_doc_service_registrations` | Services |
| Contacts | `mt_doc_contacts`, `mt_doc_contact_links` | Contacts |
| Comments | `mt_doc_comments` | Comments |
| Tagging | `mt_doc_tagging_vocabularies`, `mt_doc_tagging_terms`, `mt_doc_tagging_term_assignments` | Tagging |
| Documents | `mt_doc_document_templates`, `mt_doc_document_renders` | Documents |
| Forms | `mt_doc_public_forms`, `mt_doc_form_email_verifications`, `mt_doc_form_email_budgets` | Forms |
| Reports | `mt_doc_report_definitions` | Reports |
| Notifications | `mt_doc_notifications` | Notifications |
| Pwa | `mt_doc_pwa_installs` | none (no migration file) |
| DeviceTrust | `mt_doc_devices` | none (no migration file) |
| Stock reservations (core) | `barako_stock_counters`, `barako_stock_holds`, `barako_stock_movements` | core, `5.2.0/580-stock-reservations` |

Qr, Analytics.Umami, CdnPurge, Secrets.Aws and Secrets.Gcp made no tables.

The stock reservation tables belonged to core, not to a module. Its drop script is
`5.2.0/rollback-580-stock-reservations.sql`. If you drop them, also remove the ledger row:

```sql
DELETE FROM public.barako_migrations WHERE owner = 'core' AND id = '5.2.0/580-stock-reservations';
```
