using barakoCMS.Models;

namespace BarakoCMS.Forms.Features.Submissions;

/// <summary>Route <c>name</c> plus an optional date window, on top of paging.</summary>
public class SubmissionsRequest : PaginatedRequest
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Inclusive lower bound on the submission time, UTC.</summary>
    public DateTime? From { get; set; }

    /// <summary>Inclusive upper bound on the submission time, UTC.</summary>
    public DateTime? To { get; set; }
}

public class SubmissionResponse
{
    public Guid Id { get; set; }
    public string FormName { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTime SubmittedAt { get; set; }

    internal static SubmissionResponse From(FormSubmission s) => new()
    {
        Id = s.Id,
        FormName = s.FormName,
        Data = s.Data,
        SubmittedAt = s.SubmittedAt,
    };
}
