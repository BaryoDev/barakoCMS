using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Security;

/// <summary>
/// A database password the repository shows as an example is refused outside Development. The
/// compose files, env examples and docs all print one, so a stack that kept it runs on a public
/// password.
/// </summary>
public class DatabasePasswordGuardTests
{
    public static TheoryData<string> ExamplePasswords() => new()
    {
        "postgres",                           // docker-compose.yml default, docs/your-first-module.md
        "change-me-to-a-strong-password",     // quickstart/.env.example
        "changeme-in-production",
        "local-only-password",
        "REPLACE_THIS_WITH_REAL_PASSWORD",    // k8s/02-secret.yaml
    };

    private static string Connection(string password) =>
        $"Host=postgres;Database=barako_cms;Username=postgres;Password={password}";

    [Theory]
    [MemberData(nameof(ExamplePasswords))]
    public void An_example_password_is_refused_outside_Development(string example)
    {
        var act = () => DatabasePasswordGuard.Validate(Connection(example), isDevelopment: false);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("ConnectionStrings:DefaultConnection").And.Contain("DATABASE_URL");
        message.Should().NotContain(example, "the message names the setting and the rule, never the value");
    }

    [Theory]
    [MemberData(nameof(ExamplePasswords))]
    public void An_example_password_is_accepted_in_Development(string example)
    {
        var act = () => DatabasePasswordGuard.Validate(Connection(example), isDevelopment: true);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("  POSTGRES ")]
    [InlineData("Change-Me-To-A-Strong-Password")]
    [InlineData("replace_this_with_anything")]
    public void The_comparison_is_trimmed_and_ignores_case(string password)
    {
        var act = () => DatabasePasswordGuard.Validate($"Host=db;Username=u;Password=\"{password}\"", isDevelopment: false);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("a-random-password-0f3c9d81b2")]
    [InlineData("postgres-but-longer-and-random")]
    public void A_password_that_is_not_an_example_is_accepted(string password)
    {
        var act = () => DatabasePasswordGuard.Validate(Connection(password), isDevelopment: false);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Host=db;Username=u;Database=d")]
    [InlineData("this is not a connection string")]
    public void A_missing_or_unreadable_connection_string_is_left_to_the_real_resolve(string? connectionString)
    {
        var act = () => DatabasePasswordGuard.Validate(connectionString, isDevelopment: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void The_Pwd_keyword_is_read_as_the_password()
    {
        var act = () => DatabasePasswordGuard.Validate("Host=db;Username=u;Pwd=postgres", isDevelopment: false);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_password_in_a_DATABASE_URL_is_the_one_checked()
    {
        // The app resolves DATABASE_URL ahead of ConnectionStrings, so the guard reads what the
        // resolve produced. An app that checked only DefaultConnection would pass this.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "postgres://app:postgres@db.internal:5432/barako",
                ["ConnectionStrings:DefaultConnection"] = Connection("a-random-password-0f3c9d81b2"),
            })
            .Build();

        var resolved = ServiceCollectionExtensions.ResolveConnectionString(configuration, isDevelopment: false);
        var act = () => DatabasePasswordGuard.Validate(resolved, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("quickstart/.env.example", "DB_PASSWORD=")]
    public void The_password_in_an_env_example_is_refused(string file, string prefix)
    {
        // The file people copy to .env and are told to edit. An unedited copy must not boot.
        var line = File.ReadAllLines(Path.Combine(RepoPaths.Root, file))
            .Single(l => l.StartsWith(prefix, StringComparison.Ordinal));

        var act = () => DatabasePasswordGuard.Validate(Connection(line[prefix.Length..]), isDevelopment: false);

        act.Should().Throw<InvalidOperationException>();
    }
}
