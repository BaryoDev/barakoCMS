using barakoCMS.Infrastructure.Middleware;
using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #903: every log event written while a request runs carries the slug of the tenant the
/// request resolved to, and a request with no tenant carries none.
/// </summary>
public class TenantLogEnrichmentTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static async Task<LogEvent> LogDuringRequestAsync(TenantContext tenant, string message)
    {
        var sink = new CaptureSink();
        var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();

        var middleware = new TenantLogEnrichmentMiddleware(async _ =>
        {
            await Task.Yield();
            logger.Information(message);
        });

        await middleware.InvokeAsync(new DefaultHttpContext(), tenant);

        sink.Events.Should().ContainSingle();
        return sink.Events[0];
    }

    private static string? TenantOf(LogEvent e) =>
        e.Properties.TryGetValue(TenantLogEnrichmentMiddleware.PropertyName, out var v)
            ? ((ScalarValue)v).Value as string
            : null;

    [Fact]
    public async Task Each_tenants_request_logs_its_own_slug()
    {
        var a = await LogDuringRequestAsync(new TenantContext { Slug = "tenant-a" }, "from a");
        var b = await LogDuringRequestAsync(new TenantContext { Slug = "tenant-b" }, "from b");

        TenantOf(a).Should().Be("tenant-a");
        TenantOf(b).Should().Be("tenant-b");
    }

    [Fact]
    public async Task A_request_on_the_default_tenant_logs_no_tenant_property()
    {
        var e = await LogDuringRequestAsync(new TenantContext(), "default");

        e.Properties.Should().NotContainKey(TenantLogEnrichmentMiddleware.PropertyName);
    }

    [Fact]
    public async Task A_blank_slug_is_not_logged_as_an_empty_string()
    {
        var e = await LogDuringRequestAsync(new TenantContext { Slug = " " }, "blank");

        e.Properties.Should().NotContainKey(TenantLogEnrichmentMiddleware.PropertyName);
    }

    [Fact]
    public async Task The_tenant_property_is_gone_once_the_request_has_finished()
    {
        var sink = new CaptureSink();
        var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        var middleware = new TenantLogEnrichmentMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(new DefaultHttpContext(), new TenantContext { Slug = "tenant-a" });
        logger.Information("after");

        sink.Events.Should().ContainSingle();
        sink.Events[0].Properties.Should().NotContainKey(TenantLogEnrichmentMiddleware.PropertyName);
    }

    [Fact]
    public async Task A_slug_set_after_the_middleware_ran_is_the_one_logged()
    {
        var sink = new CaptureSink();
        var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        var tenant = new TenantContext();
        var middleware = new TenantLogEnrichmentMiddleware(_ =>
        {
            tenant.Slug = "key-tenant";
            logger.Information("after auth");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(new DefaultHttpContext(), tenant);

        sink.Events.Should().ContainSingle();
        TenantOf(sink.Events[0]).Should().Be("key-tenant");
    }
}
