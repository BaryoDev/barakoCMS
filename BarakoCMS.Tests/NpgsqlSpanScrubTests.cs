using System.Diagnostics;
using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: Npgsql's span of a database command, as it would be exported. The statement stays;
/// the parameter values, the connection string and the database user do not, and a failed command
/// is not exported, because its event quotes the server's message.
/// </summary>
/// <remarks>
/// The commands run against the suite's real database through Npgsql's own instrumentation, so
/// what is asserted is what this Npgsql version actually records. Each statement carries a marker
/// comment the test made up, which is how its span is found among the commands other tests run.
/// </remarks>
[Collection("Sequential")]
public class NpgsqlSpanScrubTests
{
    private const string Secret = "a-parameter-value-9b31e";

    private readonly IntegrationTestFixture _fixture;

    public NpgsqlSpanScrubTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task An_npgsql_span_carries_the_statement_and_no_parameter_value_or_credential()
    {
        var parentSource = "BarakoCMS.Tests.Npgsql." + Guid.NewGuid().ToString("N");
        using var source = new ActivitySource(parentSource);
        using var capture = new SpanCapture(parentSource, SpanScrubber.DatabaseSource);
        var marker = "spans-" + Guid.NewGuid().ToString("N");

        using (source.StartActivity("a request"))
        {
            await using var connection = Store.Storage.Database.CreateConnection();
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select @value::text /* {marker} */";
            command.Parameters.AddWithValue("value", Secret);
            (await command.ExecuteScalarAsync(Ct)).Should().Be(Secret);
        }

        var spans = DatabaseSpans(capture, marker);
        spans.Should().HaveCount(1);
        var tags = spans[0].TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString() ?? "");

        tags.Should().NotBeEmpty();
        tags.Keys.Should().BeSubsetOf(SpanScrubber.DatabaseTags);
        tags.Should().ContainKey("db.statement").WhoseValue.Should().Contain(marker);
        tags.Should().NotContainKeys("db.connection_string", "db.user", "db.connection_id");
        tags.Values.Should().NotContain(v => v.Contains(Secret), "a parameter value is sent apart from the statement and never exported");
        tags.Values.Should().NotContain(v => v.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failed_command_is_not_exported_because_its_event_quotes_the_server()
    {
        var parentSource = "BarakoCMS.Tests.Npgsql." + Guid.NewGuid().ToString("N");
        using var source = new ActivitySource(parentSource);
        using var capture = new SpanCapture(parentSource, SpanScrubber.DatabaseSource);
        var failing = "spans-" + Guid.NewGuid().ToString("N");
        var working = "spans-" + Guid.NewGuid().ToString("N");

        using (source.StartActivity("a request"))
        {
            await using var connection = Store.Storage.Database.CreateConnection();
            await connection.OpenAsync(Ct);

            await using (var command = connection.CreateCommand())
            {
                // Postgres answers with "invalid input syntax for type integer" and the value.
                command.CommandText = $"select cast(@value as integer) /* {failing} */";
                command.Parameters.AddWithValue("value", Secret);
                var act = () => command.ExecuteScalarAsync(Ct);
                (await act.Should().ThrowAsync<PostgresException>()).Which.Message.Should().Contain(Secret,
                    "the server's message quotes the value, which is the leak being tested");
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"select 1 /* {working} */";
                await command.ExecuteScalarAsync(Ct);
            }
        }

        DatabaseSpans(capture, working).Should().HaveCount(1, "the listener heard this connection, so a missing span below means it was dropped");
        DatabaseSpans(capture, failing).Should().BeEmpty();
        capture.Exported.SelectMany(s => s.TagObjects).Select(t => t.Value?.ToString() ?? "")
            .Should().NotContain(v => v.Contains(Secret));
    }

    [Fact]
    public async Task A_command_with_no_parent_span_is_not_exported()
    {
        using var capture = new SpanCapture(SpanScrubber.DatabaseSource);
        var marker = "spans-" + Guid.NewGuid().ToString("N");
        var previous = Activity.Current;
        Activity.Current = null;

        try
        {
            await using var connection = Store.Storage.Database.CreateConnection();
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select 1 /* {marker} */";
            await command.ExecuteScalarAsync(Ct);
        }
        finally
        {
            Activity.Current = previous;
        }

        DatabaseSpans(capture, marker).Should().BeEmpty(
            "a command no request or job started is a background poll, and those would be most of what a collector gets");
    }

    private static List<Activity> DatabaseSpans(SpanCapture capture, string marker) =>
        capture.Exported
            .Where(s => s.Source.Name == SpanScrubber.DatabaseSource
                && (s.GetTagItem("db.statement") as string)?.Contains(marker) == true)
            .ToList();
}
