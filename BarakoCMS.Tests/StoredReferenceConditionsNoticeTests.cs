using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What the start-up notice reads: the roles holding a dotted condition that follows a reference
/// in none of the tenants it is asked about. Stored straight into the database, because no
/// endpoint accepts these any more.
/// </summary>
/// <remarks>
/// Roles are stored once for every tenant, and this database holds the roles of every other test
/// class, so each assertion is about the roles this test stored, found by id.
/// </remarks>
[Collection("Sequential")]
public class StoredReferenceConditionsNoticeTests
{
    private readonly IntegrationTestFixture _fixture;

    public StoredReferenceConditionsNoticeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Role Holding(string slug, string key, bool onCreate = false, object? expected = null)
    {
        var rule = new PermissionRule
        {
            Enabled = true,
            Conditions = new Dictionary<string, object>
            {
                [key] = new Dictionary<string, object> { ["_eq"] = expected ?? "x" },
            },
        };

        return new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Notice_{Guid.NewGuid():n}",
            Permissions =
            [
                onCreate
                    ? new ContentTypePermission { ContentTypeSlug = slug, Create = rule }
                    : new ContentTypePermission { ContentTypeSlug = slug, Read = rule },
            ],
        };
    }

    [Fact]
    public async Task A_role_is_named_when_its_dotted_condition_follows_a_reference_in_no_tenant()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tag = Guid.NewGuid().ToString("n")[..8];
        var first = $"refnotice-a-{tag}";
        var second = $"refnotice-b-{tag}";
        var classes = $"noticeclass{tag}";
        var enrollments = $"noticeenrol{tag}";

        // The two types exist in the first tenant only.
        await using (var session = store.LightweightSession(first))
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = classes, DisplayName = classes,
                Fields = [new FieldDefinition { Name = "InstructorUser", DisplayName = "Instructor", Type = "string" }],
            });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = enrollments, DisplayName = enrollments,
                Fields =
                [
                    new FieldDefinition { Name = "Student", DisplayName = "Student", Type = "string" },
                    new FieldDefinition { Name = "Class", DisplayName = "Class", Type = "reference", ReferenceType = classes },
                ],
            });
            await session.SaveChangesAsync(Ct);
        }

        // Three roles hold the same key on the same content type. One follows a reference. The
        // other two are refused for the rule they sit on and for the value they compare, so the
        // key alone cannot say which role to name.
        var follows = Holding(enrollments, "Class.InstructorUser");
        var onCreate = Holding(enrollments, "Class.InstructorUser", onCreate: true);
        var aNumber = Holding(enrollments, "Class.InstructorUser", expected: 42L);
        var notAReference = Holding(enrollments, "Student.InstructorUser");
        var twoHops = Holding(enrollments, "Class.Teacher.InstructorUser");
        var noDot = Holding(enrollments, "Student");

        var roles = new[] { follows, onCreate, aNumber, notAReference, twoHops, noDot };
        var refused = new[] { onCreate.Id, aNumber.Id, notAReference.Id, twoHops.Id };

        try
        {
            await using (var session = store.LightweightSession())
            {
                foreach (var role in roles)
                    session.Store(role);
                await session.SaveChangesAsync(Ct);
            }

            var inBoth = (await StoredReferenceConditionsNotice.ReadAsync(store, [first, second], Ct))
                .Select(role => role.Id).ToList();

            inBoth.Should().Contain(refused);
            inBoth.Should().NotContain(follows.Id, "it follows a reference in the first tenant, whatever another role does with the same key");
            inBoth.Should().NotContain(noDot.Id, "a key with no dot is a field of the row, as it always was");

            // Asked about the second tenant alone, where neither type exists, it follows nothing.
            var inSecond = (await StoredReferenceConditionsNotice.ReadAsync(store, [second], Ct))
                .Select(role => role.Id).ToList();

            inSecond.Should().Contain(refused.Append(follows.Id));
            inSecond.Should().NotContain(noDot.Id);

            // With no tenant to ask, every dotted condition counts.
            var nowhere = await StoredReferenceConditionsNotice.ReadAsync(store, [], Ct);

            nowhere.Should().Contain(role => role.Id == follows.Id && role.Name == follows.Name);
            nowhere.Select(role => role.Id).Should().NotContain(noDot.Id);
        }
        finally
        {
            // Roles are read by every later run of the notice, and a definition keeps its tenant
            // among the partitions the background passes visit.
            await using (var session = store.LightweightSession())
            {
                foreach (var role in roles)
                    session.Delete<Role>(role.Id);
                await session.SaveChangesAsync(Ct);
            }

            await using (var session = store.LightweightSession(first))
            {
                session.DeleteWhere<ContentTypeDefinition>(d => d.Name == classes || d.Name == enrollments);
                await session.SaveChangesAsync(Ct);
            }
        }
    }
}
