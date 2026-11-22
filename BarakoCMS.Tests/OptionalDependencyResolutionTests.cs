using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using BarakoCMS.Email.Smtp;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Each service below has one public constructor, and the dependencies a host does not always
/// register default to null on it. A host that leaves one out must still resolve the service. With
/// a fallback overload gone, the default is the only thing that makes that true, so a default
/// removed by mistake fails here and not at the first request.
/// </summary>
public class OptionalDependencyResolutionTests
{
    private static ServiceCollection Services() => new();

    [Fact]
    public void ContentWriter_resolves_without_a_batch_transaction_or_a_logger()
    {
        var services = Services();
        services.AddSingleton(Mock.Of<IDocumentSession>());
        services.AddSingleton(Mock.Of<IContentSourcingPolicy>());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<ContentWriter>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ContentWriter>().Should().NotBeNull();
    }

    [Fact]
    public void ContentValidatorService_resolves_without_a_file_store_or_data_sources()
    {
        var services = Services();
        services.AddSingleton(Mock.Of<IQuerySession>());
        services.AddScoped<ContentValidatorService>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ContentValidatorService>().Should().NotBeNull();
    }

    [Fact]
    public void OtpService_resolves_without_the_auth_mail_queue()
    {
        var services = Services();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IDocumentSession>());
        services.AddSingleton(Mock.Of<IEmailService>());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<OtpService>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<OtpService>().Should().NotBeNull();
    }

    [Fact]
    public void TenantSessionFactory_resolves_without_a_batch_transaction()
    {
        var services = Services();
        services.AddSingleton(Mock.Of<IDocumentStore>());
        services.AddSingleton(new TenantContext());
        services.AddScoped<TenantSessionFactory>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TenantSessionFactory>().Should().NotBeNull();
    }

    [Fact]
    public void SmtpEmailService_resolves_without_a_logger_or_tenant_settings()
    {
        var services = Services();
        services.AddOptions<SmtpOptions>();
        services.AddSingleton(Mock.Of<IEmailSettingsProvider>());
        services.AddScoped<SmtpEmailService>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<SmtpEmailService>().Should().NotBeNull();
    }
}
