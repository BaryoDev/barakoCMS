using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;
using System.Security.Claims;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

// List pending approvals for current user
public class ApprovalSummary
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ContentType { get; set; } = "";
    public Guid ContentId { get; set; }
    public ApprovalType ApprovalType { get; set; }
    public ApprovalStatus Status { get; set; }
    public int TotalApprovers { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int PendingCount { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class ListPendingApprovalsResponse
{
    public List<ApprovalSummary> Approvals { get; set; } = new();
}

public class ListPendingApprovalsEndpoint : EndpointWithoutRequest<ListPendingApprovalsResponse>
{
    private readonly IApprovalService _approvalService;

    public ListPendingApprovalsEndpoint(IApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/approvals/pending");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        var approvals = await _approvalService.GetPendingApprovalsAsync(userId, ct);

        await SendAsync(new ListPendingApprovalsResponse
        {
            Approvals = approvals.Select(a => ToSummary(a)).ToList()
        }, cancellation: ct);
    }

    private static ApprovalSummary ToSummary(ApprovalRequest a)
    {
        return new ApprovalSummary
        {
            Id = a.Id,
            Title = a.Title,
            Description = a.Description,
            ContentType = a.ContentType,
            ContentId = a.ContentId,
            ApprovalType = a.ApprovalType,
            Status = a.Status,
            TotalApprovers = a.Approvers.Count,
            ApprovedCount = a.Approvers.Count(ap => ap.Status == ApproverStatus.Approved),
            RejectedCount = a.Approvers.Count(ap => ap.Status == ApproverStatus.Rejected),
            PendingCount = a.Approvers.Count(ap => ap.Status == ApproverStatus.Pending),
            RequestedAt = a.RequestedAt,
            ExpiresAt = a.ExpiresAt
        };
    }
}

// Get approval details
public class GetApprovalRequest
{
    public Guid Id { get; set; }
}

public class ApproverSummary
{
    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public string? Role { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public ApproverStatus Status { get; set; }
    public ApprovalDecision? Decision { get; set; }
    public string? Comments { get; set; }
    public DateTime? RespondedAt { get; set; }
}

public class GetApprovalResponse
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ContentType { get; set; } = "";
    public Guid ContentId { get; set; }
    public Guid WorkflowId { get; set; }
    public ApprovalType ApprovalType { get; set; }
    public int? ApprovalThreshold { get; set; }
    public ApprovalStatus Status { get; set; }
    public List<ApproverSummary> Approvers { get; set; } = new();
    public Guid RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class GetApprovalEndpoint : Endpoint<GetApprovalRequest, GetApprovalResponse>
{
    private readonly IApprovalService _approvalService;

    public GetApprovalEndpoint(IApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/approvals/{Id}");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(GetApprovalRequest req, CancellationToken ct)
    {
        var approval = await _approvalService.GetApprovalAsync(req.Id, ct);

        if (approval == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        await SendAsync(new GetApprovalResponse
        {
            Id = approval.Id,
            Title = approval.Title,
            Description = approval.Description,
            ContentType = approval.ContentType,
            ContentId = approval.ContentId,
            WorkflowId = approval.WorkflowId,
            ApprovalType = approval.ApprovalType,
            ApprovalThreshold = approval.ApprovalThreshold,
            Status = approval.Status,
            Approvers = approval.Approvers.Select(ap => new ApproverSummary
            {
                UserId = ap.UserId,
                Email = ap.Email,
                Role = ap.Role,
                Name = ap.Name,
                Order = ap.Order,
                Status = ap.Status,
                Decision = ap.Decision,
                Comments = ap.Comments,
                RespondedAt = ap.RespondedAt
            }).ToList(),
            RequestedBy = approval.RequestedBy,
            RequestedAt = approval.RequestedAt,
            ExpiresAt = approval.ExpiresAt,
            CompletedAt = approval.CompletedAt
        }, cancellation: ct);
    }
}

// Submit approval response
public class SubmitApprovalRequest
{
    public Guid Id { get; set; }
    public bool Approved { get; set; }
    public string? Comments { get; set; }
}

public class SubmitApprovalResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public ApprovalStatus NewStatus { get; set; }
}

public class SubmitApprovalEndpoint : Endpoint<SubmitApprovalRequest, SubmitApprovalResponse>
{
    private readonly IApprovalService _approvalService;

    public SubmitApprovalEndpoint(IApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/approvals/{Id}/respond");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(SubmitApprovalRequest req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        var result = await _approvalService.SubmitResponseAsync(
            req.Id,
            userId,
            req.Approved,
            req.Comments,
            ct);

        if (result.Success)
        {
            await SendAsync(new SubmitApprovalResponse
            {
                Success = true,
                Message = result.Message,
                NewStatus = result.NewStatus
            }, cancellation: ct);
        }
        else
        {
            await SendAsync(new SubmitApprovalResponse
            {
                Success = false,
                Error = result.Error
            }, 400, ct);
        }
    }
}

// Get approvals by content
public class GetApprovalsByContentRequest
{
    [QueryParam]
    public string ContentType { get; set; } = "";

    [QueryParam]
    public Guid ContentId { get; set; }
}

public class GetApprovalsByContentEndpoint : Endpoint<GetApprovalsByContentRequest, ListPendingApprovalsResponse>
{
    private readonly IApprovalService _approvalService;

    public GetApprovalsByContentEndpoint(IApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/approvals/by-content");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(GetApprovalsByContentRequest req, CancellationToken ct)
    {
        var approvals = await _approvalService.GetApprovalsByContentAsync(
            req.ContentType,
            req.ContentId,
            ct);

        await SendAsync(new ListPendingApprovalsResponse
        {
            Approvals = approvals.Select(a => new ApprovalSummary
            {
                Id = a.Id,
                Title = a.Title,
                Description = a.Description,
                ContentType = a.ContentType,
                ContentId = a.ContentId,
                ApprovalType = a.ApprovalType,
                Status = a.Status,
                TotalApprovers = a.Approvers.Count,
                ApprovedCount = a.Approvers.Count(ap => ap.Status == ApproverStatus.Approved),
                RejectedCount = a.Approvers.Count(ap => ap.Status == ApproverStatus.Rejected),
                PendingCount = a.Approvers.Count(ap => ap.Status == ApproverStatus.Pending),
                RequestedAt = a.RequestedAt,
                ExpiresAt = a.ExpiresAt
            }).ToList()
        }, cancellation: ct);
    }
}
