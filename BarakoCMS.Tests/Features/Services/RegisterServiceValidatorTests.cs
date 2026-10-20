using BarakoCMS.Services.Features.Register;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Services;

public class RegisterServiceValidatorTests
{
    private static RegisterServiceRequest Valid() => new()
    {
        Name = "billing",
        MessageTypes = ["invoice.created"],
        DeliveryUrl = "https://billing.example.com/inbox",
    };

    private static List<string> Errors(RegisterServiceRequest req) =>
        new RegisterServiceValidator().Validate(req).Errors.Select(e => e.PropertyName).ToList();

    [Fact]
    public void A_complete_registration_is_accepted()
    {
        Errors(Valid()).Should().BeEmpty();
    }

    [Fact]
    public void A_topic_can_stand_in_for_a_delivery_url()
    {
        var req = Valid();
        req.DeliveryUrl = null;
        req.Topic = "billing-events";

        Errors(req).Should().BeEmpty();
    }

    [Fact]
    public void A_service_with_nowhere_to_send_messages_is_refused()
    {
        var req = Valid();
        req.DeliveryUrl = null;

        Errors(req).Should().Contain("DeliveryUrl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("-leading")]
    public void A_name_that_is_not_a_plain_route_segment_is_refused(string name)
    {
        var req = Valid();
        req.Name = name;

        Errors(req).Should().Contain("Name");
    }

    [Theory]
    [InlineData("ftp://x.example.com")]
    [InlineData("/relative")]
    [InlineData("https://user:pass@x.example.com")]
    public void A_url_must_be_absolute_http_without_credentials(string url)
    {
        var req = Valid();
        req.DeliveryUrl = url;
        req.HealthUrl = url;

        var errors = Errors(req);

        errors.Should().Contain("DeliveryUrl");
        errors.Should().Contain("HealthUrl");
    }

    [Fact]
    public void A_registration_with_no_message_types_is_refused()
    {
        var req = Valid();
        req.MessageTypes = [];

        Errors(req).Should().Contain("MessageTypes");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(86_401)]
    public void An_interval_outside_the_allowed_range_is_refused(int seconds)
    {
        var req = Valid();
        req.HeartbeatIntervalSeconds = seconds;

        Errors(req).Should().Contain("HeartbeatIntervalSeconds");
    }
}
