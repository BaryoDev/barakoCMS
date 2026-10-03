using barakoCMS.Features.ContentType.Blueprints;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A blueprint file may declare a type's uniqueness rules. One the content type endpoints would
/// refuse is listed with the reason, and one they accept is carried into the types an apply stores.
/// </summary>
/// <remarks>The catalog alone, over a directory of this class's own, with no host and no database.</remarks>
public class BlueprintUniquenessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"barako-unique-{Guid.NewGuid():N}");

    public BlueprintUniquenessTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "timeclock.json"), Blueprint("timeclock", "$createdBy", "Open"));
        File.WriteAllText(Path.Combine(_directory, "badclock.json"), Blueprint("badclock", "Nope", "Open"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string Blueprint(string name, string field, string state) => $$"""
        {
          "name": "{{name}}",
          "contentTypes": [
            {
              "name": "{{name}}-entry",
              "displayName": "Time entry",
              "fields": [ { "name": "Note", "displayName": "Note", "type": "string" } ],
              "lifecycle": {
                "states": [ "Open", "Closed" ],
                "initialState": "Open",
                "transitions": [ { "name": "ClockOut", "from": "Open", "to": "Closed" } ]
              },
              "uniqueness": [ { "name": "OneOpenEntryPerTeacher", "fields": [ "{{field}}" ], "whenState": "{{state}}" } ]
            }
          ]
        }
        """;

    private BlueprintCatalog Catalog() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BlueprintCatalog.PathKey] = _directory })
            .Build(),
        new ContentTypeValidatorService(),
        NullLogger<BlueprintCatalog>.Instance);

    [Fact]
    public void A_blueprint_whose_rule_names_a_field_the_type_does_not_declare_is_listed_with_the_reason()
    {
        var entry = Catalog().Find("badclock");

        entry.Should().NotBeNull();
        entry!.IsValid.Should().BeFalse();
        entry.Errors.Should().ContainSingle(e => e.Contains("names the field 'Nope', which the type does not declare"));
    }

    [Fact]
    public void A_blueprint_with_a_rule_the_type_can_hold_carries_it_into_the_types_it_applies()
    {
        var entry = Catalog().Find("timeclock");

        entry.Should().NotBeNull();
        entry!.Errors.Should().BeEmpty();

        var types = BlueprintCatalog.Materialize(entry.Definition!);
        types.Should().HaveCount(1);
        types[0].Uniqueness.Should().HaveCount(1);
        types[0].Uniqueness![0].Fields.Should().Equal("$createdBy");
        types[0].Uniqueness![0].WhenState.Should().Be("Open");
    }
}
