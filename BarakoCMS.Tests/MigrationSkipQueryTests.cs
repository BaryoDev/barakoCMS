using barakoCMS.Infrastructure.Migrations;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Each file that carries a skip query, on a database that does not have its change yet: the skip
/// query has to answer false, the file has to run, and only then may the query answer true.
/// </summary>
/// <remarks>
/// <see cref="MigrationHostTests"/> holds the true side against a current schema. On its own that
/// would let any of these queries be replaced with <c>select true</c>, which is the dangerous
/// direction: a file recorded as done on a database it never ran against. Here every case builds
/// the smallest shape the file's statements need and that the release before it had, so a query that
/// is true for the wrong reason fails the first assertion.
/// </remarks>
[Collection("Sequential")]
public class MigrationSkipQueryTests
{
    private const string ImmutableTimestamp =
        "create function public.mt_immutable_timestamp(value text) returns timestamp without time zone "
        + "language sql immutable as $f$ select value::timestamp $f$;";

    private const string ImmutableTimestampTz =
        "create function public.mt_immutable_timestamptz(value text) returns timestamp with time zone "
        + "language sql immutable as $f$ select value::timestamptz $f$;";

    /// <summary>What the database looked like before each file, and what shows the file did its work.</summary>
    private static readonly Dictionary<string, (string Before, string Proof)> Cases = new(StringComparer.Ordinal)
    {
        // A 3.x event store and the two document tables the file touches, with two drafts. The file
        // moves a draft that carries a publish time to Scheduled (3) and leaves a plain draft at 0.
        ["core/4.0.0/3.x-to-4.0"] = (
            "create table public.mt_streams (id uuid primary key, snapshot jsonb, snapshot_version integer);"
            + "create table public.mt_events (seq_id bigint primary key, data jsonb);"
            + "create table public.mt_doc_contenttypedefinition (tenant_id varchar not null default '*DEFAULT*', id uuid not null, data jsonb not null);"
            + "create table public.mt_doc_contents (id uuid primary key, data jsonb not null);"
            + "insert into public.mt_doc_contents (id, data) values "
            + "('00000000-0000-0000-0000-000000000001', '{\"Status\": 0, \"ScheduledPublishAt\": \"2030-01-01T00:00:00Z\"}'), "
            + "('00000000-0000-0000-0000-000000000002', '{\"Status\": 0, \"ScheduledPublishAt\": null}');"
            + ImmutableTimestamp + ImmutableTimestampTz,
            "select (select data ->> 'Status' from public.mt_doc_contents where id = '00000000-0000-0000-0000-000000000001') = '3' "
            + "and (select data ->> 'Status' from public.mt_doc_contents where id = '00000000-0000-0000-0000-000000000002') = '0' "
            + "and to_regclass('public.mt_doc_workflow_runs') is not null"),

        ["core/4.2.0/site-share-links"] = (
            ImmutableTimestampTz,
            "select to_regclass('public.mt_doc_site_share_links_uidx_key_hash') is not null"),

        // The users table as 4.1 left it: the document column, and no normalised fields or indexes.
        ["core/4.2.0/user-normalized-identity"] = (
            "alter table public.mt_doc_users add column data jsonb not null default '{}'::jsonb;"
            + "insert into public.mt_doc_users (id, data) values (gen_random_uuid(), '{\"Username\": \" Alice \", \"Email\": \"A@Example.com\"}');",
            "select (select data ->> 'NormalizedUsername' from public.mt_doc_users) = 'alice' "
            + "and (select data ->> 'NormalizedEmail' from public.mt_doc_users) = 'a@example.com'"),

        ["core/4.3.0/collection-syncs"] = (
            "select 1;",
            "select to_regclass('public.mt_doc_collection_syncs_uidx_slug') is not null"),

        ["core/4.3.0/marten-9-37-event-store-columns"] = (
            "create table public.mt_streams (id uuid primary key);"
            + "create table public.mt_event_progression (name varchar primary key, last_seq_id bigint);",
            "select (select count(*) from information_schema.columns where table_schema = 'public' "
            + "and table_name = 'mt_event_progression') = 15"),

        // The same signature with a body that is not the 9.38 one.
        ["core/4.4.0/marten-9-38-quick-append-events"] = (
            "create function public.mt_quick_append_events(stream uuid, stream_type varchar, tenantid varchar, "
            + "event_ids uuid[], event_types varchar[], dotnet_types varchar[], bodies jsonb[], bdatas bytea[], "
            + "expected_version integer default null) returns int[] language plpgsql as $f$ begin return null; end $f$;",
            "select exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace "
            + "where n.nspname = 'public' and p.proname = 'mt_quick_append_events' and p.prosrc like '%mt_events_sequence%')"),

        ["core/4.5.0/refresh-token-hash-index"] = (
            "create table public.mt_doc_refresh_tokens (id uuid primary key, data jsonb not null);",
            "select (select indisunique from pg_index where indexrelid = to_regclass('public.mt_doc_refresh_tokens_uidx_token_hash'))"),

        // Memberships as 4.6 left them, two rows that share neither a user nor a tenant, and no
        // unique index. The file builds the index over them.
        ["core/4.7.0/membership-unique-user-tenant"] = (
            "create table public.mt_doc_memberships (id uuid primary key, data jsonb not null);"
            + "insert into public.mt_doc_memberships (id, data) values "
            + "(gen_random_uuid(), '{\"UserId\": \"00000000-0000-0000-0000-000000000001\", \"TenantSlug\": \"default\"}'), "
            + "(gen_random_uuid(), '{\"UserId\": \"00000000-0000-0000-0000-000000000001\", \"TenantSlug\": \"other\"}');",
            "select coalesce((select indisunique and indisvalid from pg_index "
            + "where indexrelid = to_regclass('public.mt_doc_memberships_uidx_user_id_tenant_slug')), false)"),

        ["Email.Resend/4.5.0/email-sent-emails"] = (
            ImmutableTimestamp,
            "select to_regclass('public.mt_doc_sent_emails_idx_at') is not null"),

        ["Files/4.2.0/stored-files-parent-index"] = (
            "create table public.mt_doc_stored_files (id uuid primary key, data jsonb not null);",
            "select to_regclass('public.mt_doc_stored_files_idx_parent_file_id') is not null"),

        ["Forms/4.2.0/forms-public-forms"] = (
            "select 1;",
            "select to_regclass('public.mt_doc_public_forms') is not null"),

        ["Forms/4.6.0/forms-email-verification"] = (
            "select 1;",
            "select to_regclass('public.mt_doc_form_email_budgets') is not null "
            + "and not (select relrowsecurity from pg_class where oid = to_regclass('public.mt_doc_form_email_verifications'))"),
    };

    public static TheoryData<string> Keys()
    {
        var keys = new TheoryData<string>();
        foreach (var key in Cases.Keys)
            keys.Add(key);
        return keys;
    }

    private readonly IntegrationTestFixture _factory;

    public MigrationSkipQueryTests(IntegrationTestFixture factory) => _factory = factory;

    private static IReadOnlyList<ShippedMigration> WithSkipQuery() =>
        ShippedMigrations
            .Discover(
            [
                new BarakoCMS.Forms.FormsModule(),
                new BarakoCMS.Files.FilesModule(),
                new BarakoCMS.Email.Resend.ResendEmailModule(),
                new BarakoCMS.ExternalAuth.ExternalAuthModule(),
            ])
            .Where(m => m.SkipWhen is not null)
            .ToList();

    /// <summary>
    /// Every skip query a first-party file ships, the ten released before the ledger and any added
    /// since, has its false side tested here.
    /// </summary>
    [Fact]
    public void Every_file_with_a_skip_query_has_a_case_here()
    {
        var keys = WithSkipQuery().Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        keys.Count.Should().BeGreaterThanOrEqualTo(12, "ten files released before the ledger, the Forms 4.6.0 file and the 4.7.0 memberships index");
        keys.Should().Equal(Cases.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task The_skip_query_is_false_before_the_file_and_true_only_after_it_has_run(string key)
    {
        var ct = TestContext.Current.CancellationToken;
        var migration = WithSkipQuery().Single(m => m.Key == key);
        var (before, proof) = Cases[key];
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        await database.ExecuteAsync(before);
        var ledger = database.Ledger();

        (await database.IsTrueAsync(migration.SkipWhen!)).Should().BeFalse(
            "this database does not have the change, and a true answer here records the file as done without running it");
        (await database.IsTrueAsync(proof)).Should().BeFalse("the proof must not hold before the file either");

        var first = await ledger.ApplyAsync([migration], _ => { }, ct);

        first.Error.Should().BeNull();
        first.Applied.Should().Equal(key);
        (await database.IsTrueAsync(proof)).Should().BeTrue("the file's own work is in the database");
        (await database.IsTrueAsync(migration.SkipWhen!)).Should().BeTrue("and now the query finds it");

        await database.ExecuteAsync("delete from public.barako_migrations");
        var second = await ledger.ApplyAsync([migration], _ => { }, ct);

        second.Error.Should().BeNull();
        second.Applied.Should().BeEmpty("with the ledger gone, as on a database migrated by hand, the file is not run again");
        second.Baselined.Should().Equal(key);
    }
}
