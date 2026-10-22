using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Notifications;
using FastEndpoints;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Notifications;

public class NotificationsModuleTests
{
    [Fact]
    public void The_module_is_named_and_states_its_http_contract_version()
    {
        IBarakoModule module = new NotificationsModule();

        module.Name.Should().Be("Notifications");
        module.HttpContractVersion.Should().Be(1);
    }

    [Fact]
    public void Every_endpoint_that_returns_a_page_takes_a_paginated_request()
    {
        var endpoints = ModuleEndpoints();

        endpoints.Should().NotBeEmpty();

        var listing = endpoints
            .Where(t => EndpointArguments(t).Response.IsGenericType
                        && EndpointArguments(t).Response.GetGenericTypeDefinition() == typeof(PaginatedResponse<>))
            .ToList();

        listing.Should().HaveCountGreaterThanOrEqualTo(2, "the own list and the admin list");
        listing.Should().OnlyContain(t => typeof(PaginatedRequest).IsAssignableFrom(EndpointArguments(t).Request));
    }

    [Fact]
    public void Every_endpoint_class_is_internal()
    {
        var endpoints = ModuleEndpoints();

        endpoints.Should().NotBeEmpty();
        endpoints.Should().OnlyContain(t => !t.IsPublic);
    }

    private static List<Type> ModuleEndpoints() =>
        typeof(NotificationsModule).Assembly.GetTypes().Where(t => !t.IsAbstract && IsEndpoint(t)).ToList();

    private static bool IsEndpoint(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Endpoint<,>)) return true;
        return false;
    }

    private static (Type Request, Type Response) EndpointArguments(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Endpoint<,>))
                return (t.GetGenericArguments()[0], t.GetGenericArguments()[1]);
        throw new InvalidOperationException();
    }
}
