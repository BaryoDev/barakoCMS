using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

// List credentials (metadata only, no secrets)
public class CredentialSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Scopes { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class ListCredentialsResponse
{
    public List<CredentialSummary> Credentials { get; set; } = new();
}

public class ListCredentialsEndpoint : EndpointWithoutRequest<ListCredentialsResponse>
{
    private readonly ICredentialService _credentialService;

    public ListCredentialsEndpoint(ICredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/credentials");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var credentials = await _credentialService.ListCredentialsAsync(ct);

        await SendAsync(new ListCredentialsResponse
        {
            Credentials = credentials.Select(c => new CredentialSummary
            {
                Id = c.Id,
                Name = c.Name,
                Type = c.Type.ToString(),
                Provider = c.Provider,
                Description = c.Description,
                Scopes = c.Scopes,
                Tags = c.Tags,
                IsActive = c.IsActive,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                ExpiresAt = c.ExpiresAt,
                LastUsedAt = c.LastUsedAt
            }).ToList()
        }, cancellation: ct);
    }
}

// Create credential
public class CreateCredentialRequest
{
    public string Name { get; set; } = "";
    public CredentialType Type { get; set; }
    public string Provider { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Scopes { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    // Credential data (will be encrypted)
    public CredentialDataInput? Data { get; set; }
}

public class CredentialDataInput
{
    // API Key
    public string? ApiKey { get; set; }
    public string? ApiKeyHeader { get; set; }

    // Basic Auth
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Bearer Token
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }

    // OAuth2
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? TokenUrl { get; set; }
    public string? RedirectUri { get; set; }

    // SMTP
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public bool? SmtpUseSsl { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }

    // Custom
    public Dictionary<string, string>? CustomFields { get; set; }
}

public class CreateCredentialResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class CreateCredentialEndpoint : Endpoint<CreateCredentialRequest, CreateCredentialResponse>
{
    private readonly CredentialService _credentialService;

    public CreateCredentialEndpoint(ICredentialService credentialService)
    {
        _credentialService = (CredentialService)credentialService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/credentials");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(CreateCredentialRequest req, CancellationToken ct)
    {
        var credential = new ActionCredential
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Type = req.Type,
            Provider = req.Provider,
            Description = req.Description ?? "",
            Scopes = req.Scopes,
            Tags = req.Tags,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (req.Data != null)
        {
            var credentialData = new CredentialData
            {
                ApiKey = req.Data.ApiKey,
                ApiKeyHeader = req.Data.ApiKeyHeader,
                Username = req.Data.Username,
                Password = req.Data.Password,
                AccessToken = req.Data.AccessToken,
                RefreshToken = req.Data.RefreshToken,
                ClientId = req.Data.ClientId,
                ClientSecret = req.Data.ClientSecret,
                AuthorizationUrl = req.Data.AuthorizationUrl,
                TokenUrl = req.Data.TokenUrl,
                RedirectUri = req.Data.RedirectUri,
                SmtpHost = req.Data.SmtpHost,
                SmtpPort = req.Data.SmtpPort,
                SmtpUseSsl = req.Data.SmtpUseSsl,
                SmtpUsername = req.Data.SmtpUsername,
                SmtpPassword = req.Data.SmtpPassword,
                FromEmail = req.Data.FromEmail,
                FromName = req.Data.FromName,
                CustomFields = req.Data.CustomFields
            };

            await _credentialService.SaveCredentialWithDataAsync(credential, credentialData, ct);
        }
        else
        {
            await _credentialService.SaveCredentialAsync(credential, ct);
        }

        await SendAsync(new CreateCredentialResponse
        {
            Id = credential.Id,
            Name = credential.Name
        }, 201, ct);
    }
}

// Delete credential
public class DeleteCredentialRequest
{
    public string Name { get; set; } = "";
}

public class DeleteCredentialEndpoint : Endpoint<DeleteCredentialRequest, object>
{
    private readonly ICredentialService _credentialService;

    public DeleteCredentialEndpoint(ICredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    public override void Configure()
    {
        Delete("/api/workflows/v2/credentials/{Name}");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(DeleteCredentialRequest req, CancellationToken ct)
    {
        await _credentialService.DeleteCredentialAsync(req.Name, ct);
        await SendNoContentAsync(ct);
    }
}

// OAuth2 flow initiation
public class InitiateOAuth2Request
{
    public string CredentialName { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}

public class InitiateOAuth2Response
{
    public string AuthorizationUrl { get; set; } = "";
}

public class InitiateOAuth2Endpoint : Endpoint<InitiateOAuth2Request, InitiateOAuth2Response>
{
    private readonly ICredentialService _credentialService;

    public InitiateOAuth2Endpoint(ICredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/credentials/oauth2/initiate");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(InitiateOAuth2Request req, CancellationToken ct)
    {
        try
        {
            var authUrl = await _credentialService.InitiateOAuth2FlowAsync(
                req.CredentialName,
                req.RedirectUri,
                ct);

            await SendAsync(new InitiateOAuth2Response
            {
                AuthorizationUrl = authUrl
            }, cancellation: ct);
        }
        catch (ArgumentException ex)
        {
            AddError(ex.Message);
            await SendErrorsAsync(400, ct);
        }
    }
}

// OAuth2 callback
public class OAuth2CallbackRequest
{
    [QueryParam]
    public string CredentialName { get; set; } = "";

    [QueryParam]
    public string Code { get; set; } = "";

    [QueryParam]
    public string State { get; set; } = "";
}

public class OAuth2CallbackResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class OAuth2CallbackEndpoint : Endpoint<OAuth2CallbackRequest, OAuth2CallbackResponse>
{
    private readonly ICredentialService _credentialService;

    public OAuth2CallbackEndpoint(ICredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/credentials/oauth2/callback");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(OAuth2CallbackRequest req, CancellationToken ct)
    {
        var success = await _credentialService.CompleteOAuth2FlowAsync(
            req.CredentialName,
            req.Code,
            req.State,
            ct);

        if (success)
        {
            await SendAsync(new OAuth2CallbackResponse
            {
                Success = true
            }, cancellation: ct);
        }
        else
        {
            await SendAsync(new OAuth2CallbackResponse
            {
                Success = false,
                Error = "Failed to complete OAuth2 flow. State may be invalid or expired."
            }, 400, ct);
        }
    }
}
