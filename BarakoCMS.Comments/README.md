# BarakoCMS.Comments

A comment thread on any record. A comment can reply to another comment on the same record. Reading
or adding a comment needs read access to the record itself, so nobody sees a thread on an entry
they cannot read.

## Install

```sh
dotnet add package BarakoCMS.Comments
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
modules run (`BarakoCMS__Modules__Enabled=Comments`). To name it by hand, put
`modules.Add(new BarakoCMS.Comments.CommentsModule())` in the `AddBarakoCMS` callback. See
`MODULES.md` in the repository.

## Endpoints

| Method | Route | Capability | Purpose |
| --- | --- | --- | --- |
| POST | `/api/comments` | `post_comments` | add a comment, or a reply with `parentId`; 201 |
| GET | `/api/comments?targetType=&targetId=` | `read_comments` | the thread on one record, oldest first, paged |
| DELETE | `/api/comments/{id}` | `post_comments` | delete a comment and its replies; the author, or a caller with `manage_comments` |

Admin holds all three by default. Grant them to other roles the usual way.

The record check comes first on every call. A record that does not exist, or lives in another
tenant, answers 404. A record the caller cannot read answers 403 and nothing is stored. An unknown
`targetType` answers 400. A comment body is 1 to 5000 characters. A `parentId` must be a comment on
the same record.

Text is stored as sent and returned as JSON text. The API never renders it as HTML, so escape it
where you display it.

## Two record types on one surface

`targetType` is `content` for a content entry. Another record type joins by registering an
`ICommentTarget`, which says whether the caller may read one record of that type:

```csharp
public sealed class TicketCommentTarget(IDocumentSession session) : ICommentTarget
{
    public string Type => "ticket";

    public async Task<CommentTargetAccess> CheckReadAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var ticket = await session.LoadAsync<Ticket>(id, ct);
        if (ticket is null) return CommentTargetAccess.NotFound;
        return CanRead(user, ticket) ? CommentTargetAccess.Allowed : CommentTargetAccess.Forbidden;
    }
}

services.AddScoped<ICommentTarget, TicketCommentTarget>();
```

`POST /api/comments` with `targetType: "ticket"` then follows the same rules as a content entry.

## Compatibility

Built for barakoCMS 5.2.0. HTTP contract version 1.

## License

[MIT](LICENSE) © BaryoDev
