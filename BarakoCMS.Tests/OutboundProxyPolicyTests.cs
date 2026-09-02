using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The guarded outbound handler ignores a system proxy unless an operator asks for one.
/// </summary>
/// <remarks>
/// With a proxy in use, the connect callback dials the proxy and the proxy then resolves and
/// connects to the real target. The address policy never sees the destination, so it is inspecting
/// the wrong hop and every guarantee it makes is void.
///
/// That matters because a system proxy arrives from an environment variable. Nobody has to
/// configure barakoCMS for `HTTP_PROXY` to exist in a container, which makes it exactly the kind of
/// ambient setting worth failing closed on.
/// </remarks>
public class OutboundProxyPolicyTests
{
    [Fact]
    public void The_guarded_handler_does_not_use_a_proxy_by_default()
    {
        using var handler = barakoCMS.Infrastructure.Http.OutboundHttpHandler.Create(
            barakoCMS.Infrastructure.Http.OutboundAddressGuard.Default);

        handler.UseProxy.Should().BeFalse(
            "a proxy resolves and connects to the target itself, so the address guard would be "
          + "checking the hop to the proxy rather than the destination");
    }

    /// <summary>
    /// The control. Without it a handler hardcoded to refuse proxies would pass the test above while
    /// leaving an operator whose egress needs one with no way to deploy.
    /// </summary>
    [Fact]
    public void An_operator_can_opt_back_in()
    {
        using var handler = barakoCMS.Infrastructure.Http.OutboundHttpHandler.Create(
            barakoCMS.Infrastructure.Http.OutboundAddressGuard.Default, allowProxy: true);

        handler.UseProxy.Should().BeTrue("Webhooks:AllowProxy exists for egress that requires one");
    }

    [Fact]
    public void Redirects_stay_off_either_way()
    {
        using var guarded = barakoCMS.Infrastructure.Http.OutboundHttpHandler.Create(
            barakoCMS.Infrastructure.Http.OutboundAddressGuard.Default);
        using var proxied = barakoCMS.Infrastructure.Http.OutboundHttpHandler.Create(
            barakoCMS.Infrastructure.Http.OutboundAddressGuard.Default, allowProxy: true);

        guarded.AllowAutoRedirect.Should().BeFalse();
        proxied.AllowAutoRedirect.Should().BeFalse(
            "a redirect is a second resolution by another route, and opting into a proxy is not "
          + "opting into that as well");
    }
}
