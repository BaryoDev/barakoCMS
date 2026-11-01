using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace BarakoCMS.Tests.Features.UserGroups;

[Collection("Integration1")]
public class UserGroupMembersTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public UserGroupMembersTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    private async Task SignInAsync()
    {
        var token = await _fixture.StoredUserTokenAsync("SuperAdmin");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> CreateGroupAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/user-groups", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await response.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        return created!.Id;
    }

    private async Task<barakoCMS.Models.UserGroup> GetGroupAsync(Guid id)
    {
        var response = await _client.GetAsync($"/api/user-groups/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<barakoCMS.Models.UserGroup>())!;
    }

    [Fact]
    public async Task A_user_listed_twice_on_create_is_removed_with_one_remove()
    {
        await SignInAsync();
        var userId = Guid.NewGuid();
        var groupId = await CreateGroupAsync(new
        {
            name = "Listed twice " + Guid.NewGuid(),
            description = "d",
            userIds = new[] { userId, userId }
        });

        (await GetGroupAsync(groupId)).UserIds.Should().ContainSingle().Which.Should().Be(userId);

        var removed = await _client.DeleteAsync($"/api/user-groups/{groupId}/users/{userId}");
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetGroupAsync(groupId)).UserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_group_created_with_null_user_ids_has_no_members_and_can_be_deleted()
    {
        await SignInAsync();
        var groupId = await CreateGroupAsync(new
        {
            name = "Null members " + Guid.NewGuid(),
            description = "d",
            userIds = (Guid[]?)null
        });

        (await GetGroupAsync(groupId)).UserIds.Should().NotBeNull().And.BeEmpty();

        var deleted = await _client.DeleteAsync($"/api/user-groups/{groupId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
