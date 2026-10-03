using System.Buffers.Binary;
using System.Text.Json;
using FluentAssertions;
using Marten;
using NSubstitute;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What an <c>inlineimage</c> field accepts on an entry write, and what delivery lets through. The
/// schema has no slug or reference field, so the validator never reaches its session.
/// </summary>
public class InlineImageFieldTests
{
    private static readonly ContentValidatorService Validator = new(Substitute.For<IQuerySession>());

    private static FieldDefinition Logo() => new() { Name = "Logo", DisplayName = "Logo", Type = "inlineimage" };

    private static Task<(bool IsValid, List<string> Errors)> WriteAsync(
        object value, FieldDefinition? field = null, Content? existing = null)
    {
        field ??= Logo();
        return Validator.ValidateFieldsAsync(
            new ContentTypeDefinition { Name = "brand", DisplayName = "Brand", Fields = [field] },
            "brand",
            new Dictionary<string, object> { [field.Name] = value },
            existing);
    }

    internal static byte[] Png(uint width, uint height, int length = 64)
    {
        var bytes = new byte[Math.Max(length, 33)];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static byte[] Gif(ushort width, ushort height)
    {
        var bytes = new byte[32];
        "GIF89a"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), height);
        return bytes;
    }

    private static byte[] Jpeg(ushort width, ushort height)
    {
        byte[] start = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
        byte[] frame = [0xFF, 0xC0, 0x00, 0x11, 0x08, 0, 0, 0, 0, 0x03, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(5), height);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(7), width);
        return [.. start, .. frame, 0xFF, 0xD9];
    }

    private static byte[] WebP(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 22);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        bytes[24] = (byte)(width - 1);
        bytes[25] = (byte)((width - 1) >> 8);
        bytes[26] = (byte)((width - 1) >> 16);
        bytes[27] = (byte)(height - 1);
        bytes[28] = (byte)((height - 1) >> 8);
        bytes[29] = (byte)((height - 1) >> 16);
        return bytes;
    }

    internal static string DataUri(string mediaType, byte[] bytes) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";

    internal static Dictionary<string, object> Image(string url, string? alt = null)
    {
        var value = new Dictionary<string, object> { ["url"] = url };
        if (alt is not null)
            value["alt"] = alt;
        return value;
    }

    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(document.domain)</script></svg>";

    [Fact]
    public async Task A_small_png_with_alt_text_is_accepted()
    {
        var (isValid, errors) = await WriteAsync(Image(DataUri("image/png", Png(32, 32)), "Acme logo"));

        isValid.Should().BeTrue(string.Join("; ", errors));
    }

    [Fact]
    public async Task Each_allowed_type_is_accepted_when_its_bytes_carry_its_signature()
    {
        (await WriteAsync(Image(DataUri("image/png", Png(16, 16))))).IsValid.Should().BeTrue();
        (await WriteAsync(Image(DataUri("image/jpeg", Jpeg(640, 480))))).IsValid.Should().BeTrue();
        (await WriteAsync(Image(DataUri("image/gif", Gif(24, 24))))).IsValid.Should().BeTrue();
        (await WriteAsync(Image(DataUri("image/webp", WebP(300, 200))))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_value_sent_as_a_json_element_is_read_like_one_from_a_request_body()
    {
        var json = JsonSerializer.Serialize(new { url = DataUri("image/png", Png(8, 8)), alt = "dot" });

        FieldTypeRegistry.IsValidValue("inlineimage", JsonDocument.Parse(json).RootElement.Clone()).Should().BeTrue();
        (await WriteAsync(JsonDocument.Parse(json).RootElement.Clone())).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Png_bytes_declared_as_svg_and_an_svg_are_both_refused_naming_the_field()
    {
        var png = Png(32, 32);
        var declaredSvg = await WriteAsync(Image(DataUri("image/svg+xml", png)));
        var realSvg = await WriteAsync(Image(DataUri("image/svg+xml", System.Text.Encoding.UTF8.GetBytes(Svg))));

        foreach (var (isValid, errors) in new[] { declaredSvg, realSvg })
        {
            isValid.Should().BeFalse();
            errors.Should().HaveCount(1);
            errors[0].Should().Contain("Logo").And.Contain("SVG is refused").And.Contain("64 KB");
        }
    }

    [Fact]
    public async Task Bytes_whose_signature_does_not_match_the_declared_type_are_refused()
    {
        var pngAsJpeg = await WriteAsync(Image(DataUri("image/jpeg", Png(32, 32))));
        var gifAsPng = await WriteAsync(Image(DataUri("image/png", Gif(32, 32))));
        var html = await WriteAsync(Image(DataUri("image/png", System.Text.Encoding.UTF8.GetBytes("<html><body>hi</body></html>"))));

        foreach (var (isValid, errors) in new[] { pngAsJpeg, gifAsPng, html })
        {
            isValid.Should().BeFalse();
            errors.Should().HaveCount(1);
            errors[0].Should().Contain("Logo").And.Contain("not the image type its data URI declares");
        }
    }

    [Fact]
    public async Task An_image_of_64_KB_is_accepted_and_one_byte_more_is_refused_naming_the_limit()
    {
        (await WriteAsync(Image(DataUri("image/png", Png(32, 32, InlineImageFields.MaxBytes))))).IsValid
            .Should().BeTrue("64 KB is the limit, not past it");

        var (isValid, errors) = await WriteAsync(Image(DataUri("image/png", Png(32, 32, InlineImageFields.MaxBytes + 1))));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Logo").And.Contain("larger than the limit").And.Contain("64 KB");
    }

    [Fact]
    public async Task A_url_longer_than_the_encoded_limit_is_refused_before_its_content_is_read()
    {
        var url = "data:image/png;base64," + new string('!', InlineImageFields.MaxUrlLength);

        var (isValid, errors) = await WriteAsync(Image(url));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("larger than the limit", "the length is checked first")
            .And.NotContain("not base64", "the content is never read");
    }

    [Fact]
    public async Task A_malformed_value_is_refused()
    {
        var png = Convert.ToBase64String(Png(32, 32));
        object[] values =
        [
            $"data:image/png;base64,{png}",
            new Dictionary<string, object> { ["alt"] = "no url" },
            new Dictionary<string, object> { ["url"] = $"data:image/png;base64,{png}", ["width"] = 32L },
            new Dictionary<string, object> { ["Url"] = $"data:image/png;base64,{png}" },
            new Dictionary<string, object> { ["url"] = $"data:image/png;base64,{png}", ["alt"] = 7L },
            Image("https://example.com/logo.png"),
            Image($"data:image/png,{png}"),
            Image($"data:IMAGE/PNG;base64,{png}"),
            Image($"data:image/png;charset=utf-8;base64,{png}"),
            Image($"data:image/png;base64,{png[..10]}\n{png[10..]}"),
            Image($"data:image/png;base64,{png[..^1]}"),
            Image("data:image/png;base64,"),
            Image($"javascript:alert(1)//{png}"),
            42L,
        ];

        values.Should().HaveCount(14);
        for (var i = 0; i < values.Length; i++)
        {
            var (isValid, errors) = await WriteAsync(values[i]);
            isValid.Should().BeFalse($"value {i} is not an inline image");
            errors.Should().HaveCount(1);
            errors[0].Should().Contain("Logo").And.Contain("64 KB");
        }
    }

    [Fact]
    public async Task An_image_with_more_pixels_than_the_limit_is_refused()
    {
        (await WriteAsync(Image(DataUri("image/png", Png(2048, 2048))))).IsValid.Should().BeTrue();

        var wide = await WriteAsync(Image(DataUri("image/png", Png(2049, 2048))));
        var huge = await WriteAsync(Image(DataUri("image/png", Png(uint.MaxValue, uint.MaxValue))));
        var jpeg = await WriteAsync(Image(DataUri("image/jpeg", Jpeg(4096, 4096))));

        foreach (var (isValid, errors) in new[] { wide, huge, jpeg })
        {
            isValid.Should().BeFalse();
            errors.Should().HaveCount(1);
            errors[0].Should().Contain("more pixels than the limit").And.Contain("2048 by 2048");
        }
    }

    [Fact]
    public async Task An_image_whose_header_gives_no_size_is_refused()
    {
        var noHeader = new byte[40];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(noHeader, 0);
        byte[] jpegWithoutFrame = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0, 0xFF, 0xD9];

        var png = await WriteAsync(Image(DataUri("image/png", noHeader)));
        var jpeg = await WriteAsync(Image(DataUri("image/jpeg", jpegWithoutFrame)));
        var gif = await WriteAsync(Image(DataUri("image/gif", Gif(0, 10))));

        foreach (var (isValid, errors) in new[] { png, jpeg, gif })
        {
            isValid.Should().BeFalse();
            errors.Should().HaveCount(1);
            errors[0].Should().Contain("width and height could not be read");
        }
    }

    [Fact]
    public async Task Alt_text_longer_than_500_characters_is_refused()
    {
        var url = DataUri("image/png", Png(32, 32));

        (await WriteAsync(Image(url, new string('a', 500)))).IsValid.Should().BeTrue();

        var (isValid, errors) = await WriteAsync(Image(url, new string('a', 501)));
        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("alt text longer than 500");
    }

    [Fact]
    public async Task No_refusal_repeats_what_was_sent()
    {
        var marker = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("qzx-marker-qzx-marker"));
        var png = Convert.ToBase64String(Png(32, 32));
        object[] values =
        [
            Image($"data:image/svg+xml;base64,{marker}"),
            Image($"data:image/png;base64,{marker}"),
            Image($"data:image/png;base64,{png}", "qzx-marker-alt" + new string('a', 600)),
            Image($"https://qzx-marker.example/{marker}"),
        ];

        var errors = new List<string>();
        foreach (var value in values)
            errors.AddRange((await WriteAsync(value)).Errors);

        errors.Should().HaveCount(4);
        foreach (var error in errors)
            error.Should().NotContain(marker).And.NotContain("qzx-marker");
    }

    [Fact]
    public async Task A_value_the_entry_already_holds_is_kept_and_a_new_one_is_checked()
    {
        var existing = new Content
        {
            Id = Guid.NewGuid(), ContentType = "brand",
            Data = new Dictionary<string, object> { ["logo"] = "text stored before the field changed type" },
        };

        (await WriteAsync("text stored before the field changed type", existing: existing)).IsValid
            .Should().BeTrue("the entry already holds it, and refusing it would block every save of the entry");

        var (isValid, errors) = await WriteAsync("other text", existing: existing);
        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_url_field_with_the_image_editor_still_takes_a_url_and_not_a_data_uri()
    {
        var field = new FieldDefinition { Name = "Hero", DisplayName = "Hero", Type = "url", Editor = "image" };

        (await WriteAsync("https://example.com/hero.png", field)).IsValid.Should().BeTrue();
        (await WriteAsync(DataUri("image/png", Png(32, 32)), field)).IsValid
            .Should().BeFalse("the image editor is a hint and opts nothing in");
    }

    [Fact]
    public void Delivery_keeps_an_allowed_inline_image_and_a_null_and_leaves_anything_else_out()
    {
        var url = DataUri("image/png", Png(32, 32));
        var definition = new ContentTypeDefinition
        {
            Name = "brand", DisplayName = "Brand", IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Logo", DisplayName = "Logo", Type = "inlineimage" },
                new FieldDefinition { Name = "Badge", DisplayName = "Badge", Type = "inlineimage" },
                new FieldDefinition { Name = "Icon", DisplayName = "Icon", Type = "inlineimage" },
                new FieldDefinition { Name = "Banner", DisplayName = "Banner", Type = "inlineimage" },
            ],
        };
        var content = new Content
        {
            Id = Guid.NewGuid(), ContentType = "brand", Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object>
            {
                ["Title"] = "Acme",
                ["logo"] = Image(url, "Acme"),
                ["Badge"] = Image(DataUri("image/svg+xml", System.Text.Encoding.UTF8.GetBytes(Svg))),
                ["Icon"] = "javascript:alert(1)",
                ["Banner"] = null!,
            },
        };

        var delivered = PublicDelivery.ToPublic(content, definition, slugField: null);
        var hooked = PublicDelivery.PublicData(content, definition);

        delivered.Should().NotBeNull();
        foreach (var data in new[] { delivered!.Data, hooked })
        {
            data.Should().HaveCount(3);
            data.Keys.Should().BeEquivalentTo(new[] { "Title", "logo", "Banner" });
            data["logo"].Should().BeEquivalentTo(Image(url, "Acme"));
            data["Banner"].Should().BeNull();
        }
    }
}
