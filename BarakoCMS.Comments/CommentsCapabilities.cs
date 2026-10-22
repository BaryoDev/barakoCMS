using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Comments;

/// <summary>
/// What this module's endpoints ask for. A capability on top of the read rule of the record the
/// comment hangs on: holding it never opens a record the caller cannot read.
/// </summary>
public static class CommentsCapabilities
{
    /// <summary>Read the comments on a record the caller can read.</summary>
    public const string ReadComments = "read_comments";

    /// <summary>Post a comment on a record the caller can read, and delete one's own.</summary>
    public const string PostComments = "post_comments";

    /// <summary>Delete anyone's comment on a record the caller can read.</summary>
    public const string ManageComments = "manage_comments";

    internal static readonly string[] All = [ReadComments, PostComments, ManageComments];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);
}
