using barakoCMS.Features.WorkflowsV2.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Service for managing workflow approval requests.
/// </summary>
public class ApprovalService : IApprovalService
{
    private readonly IDocumentSession _session;
    private readonly ILogger<ApprovalService> _logger;

    public ApprovalService(
        IDocumentSession session,
        ILogger<ApprovalService> logger)
    {
        _session = session;
        _logger = logger;
    }

    public async Task<ApprovalRequest> CreateApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        if (request.Id == Guid.Empty)
        {
            request.Id = Guid.NewGuid();
        }

        if (string.IsNullOrEmpty(request.Token))
        {
            request.Token = GenerateSecureToken();
        }

        _session.Store(request);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("Created approval request {Id} for content {ContentId}",
            request.Id, request.ContentId);

        return request;
    }

    public async Task<ApprovalRequest> CreateApprovalAsync(
        string title,
        string description,
        Guid workflowExecutionId,
        string contentType,
        Guid contentId,
        List<Guid> approverIds,
        ApprovalType approvalType = ApprovalType.Any,
        int? threshold = null,
        DateTime? expiresAt = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var approvers = approverIds.Select((id, index) => new Approver
        {
            UserId = id,
            Name = id.ToString(),
            Order = index,
            Status = ApproverStatus.Pending
        }).ToList();

        var request = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            ContentType = contentType,
            ContentId = contentId,
            WorkflowId = workflowExecutionId,
            Approvers = approvers,
            ApprovalType = approvalType,
            ApprovalThreshold = threshold,
            ExpiresAt = expiresAt,
            Metadata = metadata ?? new Dictionary<string, object>(),
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Pending,
            Token = GenerateSecureToken()
        };

        _session.Store(request);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("Created approval request {Id} for content {ContentId}",
            request.Id, contentId);

        return request;
    }

    public async Task<ApprovalRequest?> GetApprovalAsync(Guid approvalId, CancellationToken ct = default)
    {
        return await _session.LoadAsync<ApprovalRequest>(approvalId, ct);
    }

    public async Task<List<ApprovalRequest>> GetPendingApprovalsAsync(Guid userId, CancellationToken ct = default)
    {
        var approvals = await _session.Query<ApprovalRequest>()
            .Where(a => a.Status == ApprovalStatus.Pending)
            .ToListAsync(ct);

        // Filter to those where the user is an approver
        return approvals
            .Where(a => a.Approvers.Any(ap => ap.UserId == userId && ap.Status == ApproverStatus.Pending))
            .ToList();
    }

    public async Task<List<ApprovalRequest>> GetApprovalsByContentAsync(
        string contentType,
        Guid contentId,
        CancellationToken ct = default)
    {
        var approvals = await _session.Query<ApprovalRequest>()
            .Where(a => a.ContentType == contentType && a.ContentId == contentId)
            .OrderByDescending(a => a.RequestedAt)
            .ToListAsync(ct);
        return approvals.ToList();
    }

    public async Task<ApprovalResult> SubmitResponseAsync(
        Guid approvalId,
        Guid userId,
        bool approved,
        string? comments = null,
        CancellationToken ct = default)
    {
        var request = await GetApprovalAsync(approvalId, ct);

        if (request == null)
        {
            return new ApprovalResult { Success = false, Error = "Approval request not found" };
        }

        if (request.Status != ApprovalStatus.Pending)
        {
            return new ApprovalResult { Success = false, Error = $"Approval request is already {request.Status}" };
        }

        var approver = request.Approvers.FirstOrDefault(a => a.UserId == userId);
        if (approver == null)
        {
            return new ApprovalResult { Success = false, Error = "User is not an approver for this request" };
        }

        if (approver.Status != ApproverStatus.Pending)
        {
            return new ApprovalResult { Success = false, Error = "User has already responded to this request" };
        }

        if (request.ExpiresAt.HasValue && request.ExpiresAt < DateTime.UtcNow)
        {
            request.Status = ApprovalStatus.Expired;
            _session.Store(request);
            await _session.SaveChangesAsync(ct);
            return new ApprovalResult { Success = false, Error = "Approval request has expired" };
        }

        // Record decision
        approver.Status = approved ? ApproverStatus.Approved : ApproverStatus.Rejected;
        approver.Decision = approved ? ApprovalDecision.Approve : ApprovalDecision.Reject;
        approver.Comments = comments;
        approver.RespondedAt = DateTime.UtcNow;

        // Evaluate overall status
        var newStatus = EvaluateApprovalStatus(request);
        request.Status = newStatus;

        if (newStatus != ApprovalStatus.Pending)
        {
            request.CompletedAt = DateTime.UtcNow;
        }

        _session.Store(request);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} submitted {Response} for approval {ApprovalId}. Status: {Status}",
            userId, approved ? "approval" : "rejection", approvalId, newStatus);

        return new ApprovalResult
        {
            Success = true,
            NewStatus = newStatus,
            Message = GetStatusMessage(newStatus)
        };
    }

    public async Task<int> ExpireOverdueApprovalsAsync(CancellationToken ct = default)
    {
        var overdueApprovals = await _session.Query<ApprovalRequest>()
            .Where(a => a.Status == ApprovalStatus.Pending &&
                       a.ExpiresAt.HasValue &&
                       a.ExpiresAt < DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var approval in overdueApprovals)
        {
            approval.Status = ApprovalStatus.Expired;
            approval.CompletedAt = DateTime.UtcNow;
            _session.Store(approval);
        }

        await _session.SaveChangesAsync(ct);

        if (overdueApprovals.Count > 0)
        {
            _logger.LogInformation("Expired {Count} overdue approval requests", overdueApprovals.Count);
        }

        return overdueApprovals.Count;
    }

    private ApprovalStatus EvaluateApprovalStatus(ApprovalRequest request)
    {
        var approved = request.Approvers.Count(a => a.Status == ApproverStatus.Approved);
        var rejected = request.Approvers.Count(a => a.Status == ApproverStatus.Rejected);
        var total = request.Approvers.Count;

        return request.ApprovalType switch
        {
            ApprovalType.Any when approved >= 1 => ApprovalStatus.Approved,
            ApprovalType.Any when rejected >= 1 => ApprovalStatus.Rejected,
            ApprovalType.All when rejected >= 1 => ApprovalStatus.Rejected,
            ApprovalType.All when approved >= total => ApprovalStatus.Approved,
            ApprovalType.Threshold when approved >= (request.ApprovalThreshold ?? 1) => ApprovalStatus.Approved,
            ApprovalType.Sequential when rejected >= 1 => ApprovalStatus.Rejected,
            ApprovalType.Sequential when approved >= total => ApprovalStatus.Approved,
            _ => ApprovalStatus.Pending
        };
    }

    private string GetStatusMessage(ApprovalStatus status)
    {
        return status switch
        {
            ApprovalStatus.Approved => "Approval request has been approved",
            ApprovalStatus.Rejected => "Approval request has been rejected",
            ApprovalStatus.Expired => "Approval request has expired",
            ApprovalStatus.Cancelled => "Approval request was cancelled",
            _ => "Response recorded. Waiting for more approvals."
        };
    }

    private string GenerateSecureToken()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
