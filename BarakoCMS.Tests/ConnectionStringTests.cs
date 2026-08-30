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

        conn.Should().Contain("Username=user@example.com");
        conn.Should().Contain("Password=p@ss%w:rd");
        conn.Should().Contain("Database=my db");
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

    [Fact]
    public void Empty_Configuration_Falls_Back_To_Dummy_ConnectionString()
    {
        var config = CreateConfig();

        var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);

        conn.Should().Be("Server=127.0.0.1;Port=5432;Database=dummy;User Id=postgres;Password=nomartencrash;");
    }

    [Fact]
    public void StressTest_ResolveConnectionString_Under_Heavy_Parallel_Load()
    {
        var configurations = new[]
        {
            CreateConfig(databaseUrl: "postgres://user:pw@remotehost:5432/mydb"),
            CreateConfig(databaseUrl: "postgres://admin:secret@db.service:5432/main?sslmode=disable"),
            CreateConfig(databaseUrl: "postgres://usr%40domain:pass%23word@db.internal:5432/store?sslmode=verify-full"),
            CreateConfig(databaseUrl: "postgres://root:toor@remotehost/defaultdb?sslmode=require"),
            CreateConfig(databaseUrl: "Host=127.0.0.1;Port=5432;Database=rawdb;"),
            CreateConfig(defaultConnection: "Host=default;Database=standard;"),
            CreateConfig()
        };

        const int iterations = 10_000;
        System.Threading.Tasks.Parallel.For(0, iterations, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, i =>
        {
            var config = configurations[i % configurations.Length];
            var conn = barakoCMS.Extensions.ServiceCollectionExtensions.ResolveConnectionString(config);
            conn.Should().NotBeNullOrWhiteSpace();
        });
    }
}
