using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

public class ConnectionStringTests
{
    private static IConfiguration CreateConfig(string? databaseUrl = null, string? defaultConnection = null)
    {
        var values = new Dictionary<string, string?>();
        if (databaseUrl != null)
        {
            values["DATABASE_URL"] = databaseUrl;
        }

        if (defaultConnection != null)
        {
            values["ConnectionStrings:DefaultConnection"] = defaultConnection;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    [Fact]
    public void DatabaseUrl_Without_SslMode_Defaults_To_Require()
    {
        var config = CreateConfig(databaseUrl: "postgres://user:pw@remotehost:5432/mydb");

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        conn.Should().Contain("SSL Mode=Require");
        conn.Should().Contain("Host=remotehost");
        conn.Should().Contain("Port=5432");
        conn.Should().Contain("Database=mydb");
        conn.Should().Contain("Username=user");
        conn.Should().Contain("Password=pw");
    }

    /// <summary>
    /// A password containing a semicolon survives, because the connection string is built rather
    /// than interpolated.
    /// </summary>
    /// <remarks>
    /// A semicolon is legal in a Postgres password and percent-encoding it is how a URL expresses
    /// one. Unescaping turns %3B back into a literal ';', which in an interpolated connection string
    /// ends the Password key: everything after it is read as another setting, and the deployment
    /// fails with a message about an unknown keyword rather than about a password. Parsing it back
    /// with the builder is the assertion, because asserting on the string would only restate however
    /// this happens to quote today.
    /// </remarks>
    [Fact]
    public void A_password_containing_a_semicolon_survives()
    {
        var config = CreateConfig(databaseUrl: "postgres://user:pa%3Bss%3Bword@remotehost:5432/mydb");

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(conn);
        parsed.Password.Should().Be("pa;ss;word");
        parsed.Username.Should().Be("user");
        parsed.Database.Should().Be("mydb");
    }

    /// <summary>The same for the other characters a connection string treats as syntax.</summary>
    [Fact]
    public void A_password_containing_quotes_and_equals_survives()
    {
        var config = CreateConfig(databaseUrl: "postgres://user:p%3Dss%27w%22rd@remotehost:5432/mydb");

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        new Npgsql.NpgsqlConnectionStringBuilder(conn).Password.Should().Be("p=ss'w\"rd");
    }

    [Fact]
    public void DatabaseUrl_Without_Explicit_Port_Defaults_To_5432()
    {
        var config = CreateConfig(databaseUrl: "postgres://user:pw@remotehost/mydb");

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        conn.Should().Contain("Port=5432");
        conn.Should().NotContain("Port=-1");
    }

    [Fact]
    public void DatabaseUrl_With_Explicit_Port_Preserves_Port()
    {
        var config = CreateConfig(databaseUrl: "postgres://user:pw@remotehost:6543/mydb");

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        conn.Should().Contain("Port=6543");
    }

    [Theory]
    [InlineData("disable", "Disable")]
    [InlineData("DISABLE", "Disable")]
    [InlineData("allow", "Allow")]
    [InlineData("prefer", "Prefer")]
    [InlineData("require", "Require")]
    [InlineData("verify-ca", "VerifyCA")]
    [InlineData("verifyca", "VerifyCA")]
    [InlineData("verify-full", "VerifyFull")]
    [InlineData("VERIFY-FULL", "VerifyFull")]
    [InlineData("verifyfull", "VerifyFull")]
    public void DatabaseUrl_Honours_Valid_SslModes(string queryMode, string expectedSslMode)
    {
        var config = CreateConfig(databaseUrl: $"postgres://user:pw@remotehost:5432/mydb?sslmode={queryMode}&other=1");

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        conn.Should().Contain($"SSL Mode={expectedSslMode}");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("Require;Include Error Detail=false")]
    [InlineData("invalid-mode")]
    public void DatabaseUrl_With_Invalid_SslMode_Throws_ArgumentException_Naming_DatabaseUrl(string invalidMode)
    {
        var config = CreateConfig(databaseUrl: $"postgres://user:pw@remotehost:5432/mydb?sslmode={invalidMode}");

        var act = () => barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*Invalid sslmode '{invalidMode}' in DATABASE_URL*");
    }

    [Fact]
    public void DatabaseUrl_With_Percent_Encoded_Credentials_And_Database_Are_Decoded()
    {
        var config = CreateConfig(databaseUrl: "postgres://user%40example.com:p%40ss%25w%3Ard@remotehost:5432/my%20db");

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        // Parsed back rather than string-matched. The connection string is built by
        // NpgsqlConnectionStringBuilder now, which quotes a value containing a space, so asserting
        // on the raw text would only restate however Npgsql happens to quote today. The claim is
        // that the decoded value survives the round trip, and this states exactly that.
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(conn);
        parsed.Username.Should().Be("user@example.com");
        parsed.Password.Should().Be("p@ss%w:rd");
        parsed.Database.Should().Be("my db");
    }

    [Fact]
    public void Non_Uri_DatabaseUrl_Falls_Back_To_Raw_String()
    {
        const string rawConn = "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=pw";
        var config = CreateConfig(databaseUrl: rawConn);

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        conn.Should().Be(rawConn);
    }

    [Fact]
    public void Configuration_Without_DatabaseUrl_Uses_DefaultConnection()
    {
        const string defaultConn = "Host=localhost;Database=mydb;Username=postgres;Password=pw";
        var config = CreateConfig(defaultConnection: defaultConn);

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        conn.Should().Be(defaultConn);
    }

    /// <summary>
    /// No connection string anywhere is refused by name, not papered over.
    /// </summary>
    /// <remarks>
    /// This asserted the dummy fallback unconditionally, which was the behaviour when the PR was
    /// opened. 4.0 made it fail closed outside Development: a dummy string turns "nobody configured
    /// a database" into a connection refused against localhost, surfacing long after startup as
    /// something unrelated.
    ///
    /// Only the production half is asserted here. The Development half returns the dummy so
    /// design-time tooling can build a store with no database behind it, and every integration test
    /// in the suite exercises it, because the fixture forces Development. Setting
    /// ASPNETCORE_ENVIRONMENT from a test to reach it would be process-global and would land on
    /// whichever host happened to start next.
    /// </remarks>
    [Fact]
    public void No_connection_string_anywhere_is_refused_by_name()
    {
        var config = CreateConfig();

        var act = () => barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:DefaultConnection*")
            .WithMessage("*DATABASE_URL*");
    }
}
