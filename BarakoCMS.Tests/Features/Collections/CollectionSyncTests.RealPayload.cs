using System.Net;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// A real GitHub search result against the reader's per-item path cap.
/// </summary>
/// <remarks>
/// Issue 630 from <c>search/issues?q=org:BaryoDev</c>, as GitHub answered it on 24 September 2026,
/// with the body and the milestone description cut to one line and a third real label added. As answered it flattens to 198
/// paths, two under the cap. The third label takes it to 205, and <c>state_reason</c> is the 197th
/// path, so a cap counted over every path drops a field a mapping names.
/// </remarks>
public partial class CollectionSyncTests
{
    private const string RealGitHubIssue = """
    {
      "total_count": 1,
      "incomplete_results": false,
      "items": [
        {
          "url": "https://api.github.com/repos/BaryoDev/barakoCMS/issues/630",
          "repository_url": "https://api.github.com/repos/BaryoDev/barakoCMS",
          "labels_url": "https://api.github.com/repos/BaryoDev/barakoCMS/issues/630/labels{/name}",
          "comments_url": "https://api.github.com/repos/BaryoDev/barakoCMS/issues/630/comments",
          "events_url": "https://api.github.com/repos/BaryoDev/barakoCMS/issues/630/events",
          "html_url": "https://github.com/BaryoDev/barakoCMS/issues/630",
          "id": 5364106352,
          "node_id": "I_kwDOQvsMFc8AAAABP7nEcA",
          "number": 630,
          "title": "The HTTP surface has no stability policy, because section 6 excludes the thing the console consumes",
          "user": {
            "login": "arnelirobles",
            "id": 3198856,
            "node_id": "MDQ6VXNlcjMxOTg4NTY=",
            "avatar_url": "https://avatars.githubusercontent.com/u/3198856?v=4",
            "gravatar_id": "",
            "url": "https://api.github.com/users/arnelirobles",
            "html_url": "https://github.com/arnelirobles",
            "followers_url": "https://api.github.com/users/arnelirobles/followers",
            "following_url": "https://api.github.com/users/arnelirobles/following{/other_user}",
            "gists_url": "https://api.github.com/users/arnelirobles/gists{/gist_id}",
            "starred_url": "https://api.github.com/users/arnelirobles/starred{/owner}{/repo}",
            "subscriptions_url": "https://api.github.com/users/arnelirobles/subscriptions",
            "organizations_url": "https://api.github.com/users/arnelirobles/orgs",
            "repos_url": "https://api.github.com/users/arnelirobles/repos",
            "events_url": "https://api.github.com/users/arnelirobles/events{/privacy}",
            "received_events_url": "https://api.github.com/users/arnelirobles/received_events",
            "type": "User",
            "user_view_type": "public",
            "site_admin": false
          },
          "labels": [
            {
              "id": 9896223521,
              "node_id": "LA_kwDOQvsMFc8AAAACTdxjIQ",
              "url": "https://api.github.com/repos/BaryoDev/barakoCMS/labels/enhancement",
              "name": "enhancement",
              "color": "a2eeef",
              "default": true,
              "description": "New feature or request",
              "archived_at": null,
              "archived_by": null
            },
            {
              "id": 11850629103,
              "node_id": "LA_kwDOQvsMFc8AAAACwlo_7w",
              "url": "https://api.github.com/repos/BaryoDev/barakoCMS/labels/core",
              "name": "core",
              "color": "3E2418",
              "default": false,
              "description": "Belongs in the core; a module cannot provide it",
              "archived_at": null,
              "archived_by": null
            },
            {
              "id": 12088197186,
              "node_id": "LA_kwDOUOPVss8AAAAC0INAQg",
              "url": "https://api.github.com/repos/BaryoDev/barakoBrew/labels/up-for-grabs",
              "name": "up-for-grabs",
              "color": "0E8A16",
              "default": false,
              "description": "Wanted, but not currently being worked on, please take it",
              "archived_at": null,
              "archived_by": null
            }
          ],
          "state": "closed",
          "locked": false,
          "assignees": [
            {
              "login": "arnelirobles",
              "id": 3198856,
              "node_id": "MDQ6VXNlcjMxOTg4NTY=",
              "avatar_url": "https://avatars.githubusercontent.com/u/3198856?v=4",
              "gravatar_id": "",
              "url": "https://api.github.com/users/arnelirobles",
              "html_url": "https://github.com/arnelirobles",
              "followers_url": "https://api.github.com/users/arnelirobles/followers",
              "following_url": "https://api.github.com/users/arnelirobles/following{/other_user}",
              "gists_url": "https://api.github.com/users/arnelirobles/gists{/gist_id}",
              "starred_url": "https://api.github.com/users/arnelirobles/starred{/owner}{/repo}",
              "subscriptions_url": "https://api.github.com/users/arnelirobles/subscriptions",
              "organizations_url": "https://api.github.com/users/arnelirobles/orgs",
              "repos_url": "https://api.github.com/users/arnelirobles/repos",
              "events_url": "https://api.github.com/users/arnelirobles/events{/privacy}",
              "received_events_url": "https://api.github.com/users/arnelirobles/received_events",
              "type": "User",
              "user_view_type": "public",
              "site_admin": false
            }
          ],
          "milestone": {
            "url": "https://api.github.com/repos/BaryoDev/barakoCMS/milestones/8",
            "html_url": "https://github.com/BaryoDev/barakoCMS/milestone/8",
            "labels_url": "https://api.github.com/repos/BaryoDev/barakoCMS/milestones/8/labels",
            "id": 17523136,
            "node_id": "MI_kwDOQvsMFc4BC2HA",
            "number": 8,
            "title": "4.0.0",
            "description": "Shipped 7 September 2026.",
            "creator": {
              "login": "arnelirobles",
              "id": 3198856,
              "node_id": "MDQ6VXNlcjMxOTg4NTY=",
              "avatar_url": "https://avatars.githubusercontent.com/u/3198856?v=4",
              "gravatar_id": "",
              "url": "https://api.github.com/users/arnelirobles",
              "html_url": "https://github.com/arnelirobles",
              "followers_url": "https://api.github.com/users/arnelirobles/followers",
              "following_url": "https://api.github.com/users/arnelirobles/following{/other_user}",
              "gists_url": "https://api.github.com/users/arnelirobles/gists{/gist_id}",
              "starred_url": "https://api.github.com/users/arnelirobles/starred{/owner}{/repo}",
              "subscriptions_url": "https://api.github.com/users/arnelirobles/subscriptions",
              "organizations_url": "https://api.github.com/users/arnelirobles/orgs",
              "repos_url": "https://api.github.com/users/arnelirobles/repos",
              "events_url": "https://api.github.com/users/arnelirobles/events{/privacy}",
              "received_events_url": "https://api.github.com/users/arnelirobles/received_events",
              "type": "User",
              "user_view_type": "public",
              "site_admin": false
            },
            "open_issues": 0,
            "closed_issues": 149,
            "state": "closed",
            "created_at": "2026-08-28T09:30:12Z",
            "updated_at": "2026-09-08T05:29:34Z",
            "due_on": null,
            "closed_at": "2026-09-08T05:29:34Z"
          },
          "comments": 0,
          "created_at": "2026-09-06T09:21:33Z",
          "updated_at": "2026-09-07T02:44:40Z",
          "closed_at": "2026-09-07T02:44:40Z",
          "assignee": {
            "login": "arnelirobles",
            "id": 3198856,
            "node_id": "MDQ6VXNlcjMxOTg4NTY=",
            "avatar_url": "https://avatars.githubusercontent.com/u/3198856?v=4",
            "gravatar_id": "",
            "url": "https://api.github.com/users/arnelirobles",
            "html_url": "https://github.com/arnelirobles",
            "followers_url": "https://api.github.com/users/arnelirobles/followers",
            "following_url": "https://api.github.com/users/arnelirobles/following{/other_user}",
            "gists_url": "https://api.github.com/users/arnelirobles/gists{/gist_id}",
            "starred_url": "https://api.github.com/users/arnelirobles/starred{/owner}{/repo}",
            "subscriptions_url": "https://api.github.com/users/arnelirobles/subscriptions",
            "organizations_url": "https://api.github.com/users/arnelirobles/orgs",
            "repos_url": "https://api.github.com/users/arnelirobles/repos",
            "events_url": "https://api.github.com/users/arnelirobles/events{/privacy}",
            "received_events_url": "https://api.github.com/users/arnelirobles/received_events",
            "type": "User",
            "user_view_type": "public",
            "site_admin": false
          },
          "author_association": "MEMBER",
          "issue_field_values": [],
          "type": null,
          "active_lock_reason": null,
          "sub_issues_summary": {
            "total": 0,
            "completed": 0,
            "percent_completed": 0
          },
          "parent_issue_url": "https://api.github.com/repos/BaryoDev/barakoCMS/issues/629",
          "issue_dependencies_summary": {
            "blocked_by": 0,
            "total_blocked_by": 0,
            "blocking": 0,
            "total_blocking": 0
          },
          "body": "Trimmed to one line for the test.",
          "reactions": {
            "url": "https://api.github.com/repos/BaryoDev/barakoCMS/issues/630/reactions",
            "total_count": 0,
            "+1": 0,
            "-1": 0,
            "laugh": 0,
            "hooray": 0,
            "confused": 0,
            "heart": 0,
            "rocket": 0,
            "eyes": 0
          },
          "timeline_url": "https://api.github.com/repos/BaryoDev/barakoCMS/issues/630/timeline",
          "performed_via_github_app": {
            "id": 1236702,
            "client_id": "Iv23liqTIFEtdIu6Vn1r",
            "slug": "claude",
            "node_id": "A_kwHOBIuudM4AEt7e",
            "owner": {
              "login": "anthropics",
              "id": 76263028,
              "node_id": "MDEyOk9yZ2FuaXphdGlvbjc2MjYzMDI4",
              "avatar_url": "https://avatars.githubusercontent.com/u/76263028?v=4",
              "gravatar_id": "",
              "url": "https://api.github.com/users/anthropics",
              "html_url": "https://github.com/anthropics",
              "followers_url": "https://api.github.com/users/anthropics/followers",
              "following_url": "https://api.github.com/users/anthropics/following{/other_user}",
              "gists_url": "https://api.github.com/users/anthropics/gists{/gist_id}",
              "starred_url": "https://api.github.com/users/anthropics/starred{/owner}{/repo}",
              "subscriptions_url": "https://api.github.com/users/anthropics/subscriptions",
              "organizations_url": "https://api.github.com/users/anthropics/orgs",
              "repos_url": "https://api.github.com/users/anthropics/repos",
              "events_url": "https://api.github.com/users/anthropics/events{/privacy}",
              "received_events_url": "https://api.github.com/users/anthropics/received_events",
              "type": "Organization",
              "user_view_type": "public",
              "site_admin": false
            },
            "name": "Claude",
            "description": "Run Claude Code from your GitHub Pull Requests and Issues to respond to reviewer feedback, fix CI errors, or modify code, turning it into a virtual teammate that works alongside your development pipelines.\r\n\r\nThis is built on the publicly available Claude Code SDK.",
            "external_url": "https://anthropic.com/claude-code",
            "html_url": "https://github.com/apps/claude",
            "created_at": "2025-04-30T17:54:24Z",
            "updated_at": "2026-08-21T22:48:39Z",
            "permissions": {
              "actions": "write",
              "checks": "write",
              "contents": "write",
              "discussions": "write",
              "issues": "write",
              "members": "read",
              "metadata": "read",
              "pull_requests": "write",
              "repository_hooks": "write",
              "statuses": "read",
              "workflows": "write"
            },
            "events": [
              "check_run",
              "check_suite",
              "commit_comment",
              "discussion",
              "discussion_comment",
              "issues",
              "issue_comment",
              "merge_queue_entry",
              "pull_request",
              "pull_request_review",
              "pull_request_review_comment",
              "push",
              "release",
              "status",
              "sub_issues"
            ]
          },
          "state_reason": "completed",
          "pinned_comment": null,
          "score": 1.0
        }
      ]
    }
    """;

    [Fact]
    public async Task A_mapped_field_past_the_path_cap_of_a_real_github_issue_is_still_read()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, RealGitHubIssue), save: false, fields:
        [
            new FieldDefinition { Name = "title", Type = "string" },
            new FieldDefinition { Name = "reason", Type = "string" },
            new FieldDefinition { Name = "tags", Type = "string" },
        ]);

        var body = SyncBody(setup);
        body["itemsPath"] = "items";
        body["fieldMap"] = new Dictionary<string, string> { ["title"] = "title", ["reason"] = "state_reason" };
        body["keyField"] = "title";
        body["fieldRules"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "tags": { "path": "labels[].name", "join": ", " } }""");
        await PostAsync(await AdminAsync(), "/api/collection-syncs", body);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);

        var entry = (await EntriesAsync(setup.Type)).Should().ContainSingle().Subject;
        Value(entry, "tags").Should().Be("enhancement, core, up-for-grabs");
        Value(entry, "reason").Should().Be("completed");
    }
}
