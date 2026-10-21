using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class EventEnvelopeTypesTests
{
    [Fact]
    public void Header_names_are_the_strings_the_shop_already_writes()
    {
        EnvelopeHeaders.BranchId.Should().Be("branch-id");
        EnvelopeHeaders.OriginNodeId.Should().Be("origin-node-id");
        EnvelopeHeaders.OriginSequence.Should().Be("origin-sequence");
    }

    [Fact]
    public void Merge_class_and_branch_scope_read_from_the_contentTypes_shape()
    {
        var json = """{ "Name": "Customer", "MergeClass": "FieldLww", "BranchScoped": true }""";
        var type = JsonSerializer.Deserialize<ContentTypeDefinition>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            })!;

        type.MergeClass.Should().Be(MergeClass.FieldLww);
        type.BranchScoped.Should().BeTrue();
    }

    [Fact]
    public void A_type_with_neither_keeps_today_s_behaviour()
    {
        var type = new ContentTypeDefinition { Name = "note" };
        type.MergeClass.Should().BeNull();
        type.BranchScoped.Should().BeFalse();
    }

    [Fact]
    public void Create_and_merge_carry_both_settings_to_the_stored_type()
    {
        var given = new ContentTypeDefinition
        {
            Name = "Customer", DisplayName = "Customer", MergeClass = MergeClass.OwnedRequest, BranchScoped = true,
        };

        ContentTypeUpsert.Create(given).Should().Match<ContentTypeDefinition>(
            t => t.MergeClass == MergeClass.OwnedRequest && t.BranchScoped);

        var stored = new ContentTypeDefinition { Name = "customer", DisplayName = "Customer" };
        ContentTypeUpsert.MergeInto(stored, given);
        stored.MergeClass.Should().Be(MergeClass.OwnedRequest);
        stored.BranchScoped.Should().BeTrue();
    }

    [Fact]
    public void A_content_row_has_no_branch_until_one_is_set()
    {
        new Content().BranchId.Should().BeNull();
    }
}
