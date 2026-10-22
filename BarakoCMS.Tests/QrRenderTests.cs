using System.Net;
using FluentAssertions;
using Marten;
using barakoCMS.Models;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The Qr module (#807). The fixture lists <c>Token</c> for <c>qrprobe</c> and nothing for any other
/// type, so <c>qrunlisted</c> stands for an empty allowlist.
/// </summary>
[Collection("Sequential")]
public class QrRenderTests
{
    private const string Listed = "qrprobe";
    private const string Unlisted = "qrunlisted";

    private readonly IntegrationTestFixture _factory;

    public QrRenderTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SemaphoreSlim TypeGate = new(1, 1);
    private static bool _typesDeclared;

    private async Task DeclareTypesAsync()
    {
        await TypeGate.WaitAsync(Ct);
        try
        {
            if (_typesDeclared)
            {
                return;
            }

            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var name in new[] { Listed, Unlisted })
            {
                if (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == name, Ct))
                {
                    continue;
                }

                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    DisplayName = name,
                    IsPubliclyDeliverable = true,
                    Fields =
                    [
                        new FieldDefinition { Name = "Token", DisplayName = "Token", Type = "string" },
                        new FieldDefinition { Name = "Note", DisplayName = "Note", Type = "string" },
                        new FieldDefinition
                        {
                            Name = "Hidden",
                            DisplayName = "Hidden",
                            Type = "string",
                            Sensitivity = SensitivityLevel.Sensitive,
                        },
                    ],
                });
            }

            await session.SaveChangesAsync(Ct);
            _typesDeclared = true;
        }
        finally
        {
            TypeGate.Release();
        }
    }

    private async Task<Guid> StoreAsync(
        string type,
        string token,
        ContentStatus status = ContentStatus.Published,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        string? hidden = null)
    {
        await DeclareTypesAsync();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var data = new Dictionary<string, object> { ["Token"] = token, ["Note"] = "plain note" };
        if (hidden is not null)
        {
            data["Hidden"] = hidden;
        }

        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Data = data,
            Status = status,
            Sensitivity = sensitivity,
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private static string NewToken() => $"claim-{Guid.NewGuid():n}";

    private static byte[] ExpectedPng(string value, int pixelsPerModule)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    private static string ExpectedSvg(string value, int pixelsPerModule)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(pixelsPerModule);
    }

    [Fact]
    public async Task A_listed_field_renders_as_a_png_that_encodes_its_value()
    {
        var token = NewToken();
        var id = await StoreAsync(Listed, token);

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Should().NotBeEmpty();
        bytes.Should().Equal(ExpectedPng(token, 8));
        bytes.Should().NotEqual(ExpectedPng(NewToken(), 8), "a different value is a different code");
    }

    [Fact]
    public async Task A_listed_field_renders_as_an_svg_that_encodes_its_value()
    {
        var token = NewToken();
        var id = await StoreAsync(Listed, token);

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/token?format=svg&size=4", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().NotBeEmpty();
        body.Should().Be(ExpectedSvg(token, 4));
        body.Should().NotBe(ExpectedSvg(NewToken(), 4));
    }

    [Fact]
    public async Task A_field_that_is_not_listed_is_a_404()
    {
        var id = await StoreAsync(Listed, NewToken());

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Note", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_type_with_an_empty_allowlist_is_a_404()
    {
        var id = await StoreAsync(Unlisted, NewToken());

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Unlisted}/{id}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_draft_entry_is_a_404()
    {
        var id = await StoreAsync(Listed, NewToken(), status: ContentStatus.Draft);

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_entry_that_is_not_public_is_a_404()
    {
        var id = await StoreAsync(Listed, NewToken(), sensitivity: SensitivityLevel.Sensitive);

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_entry_that_does_not_exist_is_a_404()
    {
        await DeclareTypesAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{Guid.NewGuid()}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_entry_of_another_type_is_a_404_under_a_listed_type()
    {
        var id = await StoreAsync(Unlisted, NewToken());

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_value_longer_than_the_cap_is_a_404()
    {
        var id = await StoreAsync(Listed, new string('x', 1025));

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_multi_byte_value_too_big_for_a_code_is_a_404_not_a_500()
    {
        var id = await StoreAsync(Listed, new string('\u20ac', 1000));

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Token", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("size=0")]
    [InlineData("size=21")]
    [InlineData("format=gif")]
    public async Task A_bad_size_or_format_is_a_400(string query)
    {
        var id = await StoreAsync(Listed, NewToken());

        var response = await _factory.CreateClient().GetAsync($"/api/public/{Listed}/{id}/qr/Token?{query}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
