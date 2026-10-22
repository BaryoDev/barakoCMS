using barakoCMS.Core.Validation;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>Text for a json field is one JSON array or object and nothing else.</summary>
public class JsonTextFieldsTests
{
    [Theory]
    [InlineData("[], \"v\": {\"a\":1}")]
    [InlineData("[1], \"other\": 2")]
    [InlineData("{}, \"v\": [1]")]
    [InlineData("[1] x")]
    [InlineData("{\"a\":1} {\"b\":2}")]
    [InlineData("[1,")]
    [InlineData("1")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_but_one_array_or_object_is_refused(string text)
    {
        JsonTextFields.TryRead(text, out _).Should().BeFalse();
    }

    [Fact]
    public void An_array_reads_as_a_list_and_an_object_as_a_dictionary()
    {
        JsonTextFields.TryRead(" [1, {\"a\":2.5}] ", out var list).Should().BeTrue();
        list.Should().BeAssignableTo<List<object>>().Which.Should().HaveCount(2);

        JsonTextFields.TryRead("{\"a\":{\"b\":[1]}}", out var obj).Should().BeTrue();
        obj.Should().BeAssignableTo<Dictionary<string, object>>().Which.Should().ContainKey("a");
    }
}
