using System;
using barakoCMS.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

public class ConnectionStringTests
{
    [Fact]
    public void DatabaseUrl_Without_SslMode_Defaults_To_Require()
    {
        var originalEnv = Environment.GetEnvironmentVariable("DATABASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", "postgres://user:pw@remotehost:5432/mydb");
            var config = new ConfigurationBuilder().Build();

            var conn = ServiceCollectionExtensions.ResolveConnectionString(config);

            conn.Should().Contain("SSL Mode=Require");
            conn.Should().Contain("Host=remotehost");
            conn.Should().Contain("Database=mydb");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", originalEnv);
        }
    }

    [Fact]
    public void DatabaseUrl_With_SslMode_Disable_Honours_Query_Parameter()
    {
        var originalEnv = Environment.GetEnvironmentVariable("DATABASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", "postgres://user:pw@localhost:5432/mydb?sslmode=disable");
            var config = new ConfigurationBuilder().Build();

            var conn = ServiceCollectionExtensions.ResolveConnectionString(config);

            conn.Should().Contain("SSL Mode=disable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", originalEnv);
        }
    }

    [Fact]
    public void DatabaseUrl_With_SslMode_Require_Honours_Query_Parameter()
    {
        var originalEnv = Environment.GetEnvironmentVariable("DATABASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", "postgres://user:pw@remotehost:5432/mydb?sslmode=require&other=1");
            var config = new ConfigurationBuilder().Build();

            var conn = ServiceCollectionExtensions.ResolveConnectionString(config);

            conn.Should().Contain("SSL Mode=require");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", originalEnv);
        }
    }
}
